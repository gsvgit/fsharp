//----------------------------------------------------------------------------
//
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

module internal Microsoft.FSharp.Compiler.Detuple 

open Internal.Utilities
open Microsoft.FSharp.Compiler.AbstractIL 
open Microsoft.FSharp.Compiler.AbstractIL.Internal 
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library 
open Microsoft.FSharp.Compiler 

open Microsoft.FSharp.Compiler.AbstractIL.Diagnostics
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.ErrorLogger
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.Tastops.DebugPrint
open Microsoft.FSharp.Compiler.Env
open Microsoft.FSharp.Compiler.Layout
open Microsoft.FSharp.Compiler.PrettyNaming
open Microsoft.FSharp.Compiler.Lib



//
// This pass has one aim.
// - to eliminate tuples allocated at call sites (due to uncurried style)
//
// After PASS, 
//   Private, non-top-level functions fOrig which had explicit tuples at all callsites,
//   have been replaced by transformedVal taking the individual tuple fields,
//   subject to the type of the fOrig formal permitting the split.
// 
// The decisions are based on call site analysis 
//
//----------
// TUPLE COLLAPSE SIMPLIFIED.
//
// The aim of the optimization pass implemented in this module
// is to eliminate (redundant) tuple allocs arising due to calls.
// These typically arise from code written in uncurried form.
//
// Note that "top-level" functions and methods are automatically detupled in F#,
// by choice of representation. So this only applies to inner functions, and even
// then only to those not given "TLR" representation through lambda-lifting.
//
// Q: When is a tuple allocation at callsite redundant?
// A1: If the function called only wants the fields of the tuple.
// A2: If all call sites allocate a tuple argument,
//     then can factor that tuple creation into the function,
//     and hope the optimiser will eliminate it if possible.
//     e.g. if only the fields are required.
//
// The COLLAPSE transform is based on answer A2...
//
//   [[ let rec fOrig p = ... fOrig (a,b) ...
//      fOrig (x,y) ]]
//   ->
//      let rec transformedVal p1 p2 = let p = p1,p2
//                        ... (transformedVal a b) ...
//     
//      transformedVal x y
//
// Q: What about cases where some calls to fOrig provide just a tuple?
// A: If fOrig requires the original tuple argument, then this transform
//    would insert a tuple allocation inside fOrig, where none was before...
//
//----------
// IMPLEMENTATION OVERVIEW.
//
// 1. Require call-pattern info about callsites of each function, e.g.
//
//      [ (_,_) ; (_,(_,_,_)) ; _ ]
//      [ (_,_) ; (_,_)       ]
//      [ (_,_) ]
//
//    Detailing the number of arguments applied and their explicit tuple structure.
//
//    ASIDE: Efficiency note.
//           The rw pass does not change the call-pattern info,
//           so call-pattern info can be collected for all ids in pre-pass.
//
// 2. Given the above, can *CHOOSE* a call-pattern for the transformed function.
//    Informally,
//      Collapse any tuple structure if it is known at ALL call sites.
//    Formally,
//      - n = max length of call-pattern args.
//      - extend call patterns to length n with _ (no tuple info known)
//      - component-wise intersect argument tuple-structures over call patterns.
//      - gives least known call-pattern of length n.
//      - can trim to minimum non-trivual length.
//
//    [Used to] have INVARIANT on this chosen call pattern:
//
//      Have: For each argi with non-trivial tuple-structure,
//            at every call have an explicit tuple argument,
//            with (at least) that structure.
//            ----
//            Note, missing args in partial application will always
//            have trivial tuple structure in chosen call-pattern.
//
//    [PS: now defn arg projection info can override call site info]
//
// 2b.Choosing CallPattern also needs to check type of formals for the function.
//    If function is not expecting a tuple (accoring to types) do not split them.
//
// 3. Given CallPattern for selected fOrig,
//    (a) Can choose replacement formals, ybi where needed. (b, bar, means vector of formals).
//
//     cpi                | xi    | ybi
//    --------------------|-------|----------
//     UnknownTS          | xi    | SameArg xi
//     TupleTS []         | []    | SameArg []     // unit case, special case for now.
//     TupleTS ts1...tsN  | xi    | NewArgs (List.collect createFringeFormals [ts1..tsN])
//
//    (b) Can define transformedVal replacement function id.
//
// 4. Fixup defn bindings.
//
//    [[DEFN: fOrig  = LAM tps. lam x1 ...xp xq...xN. body ]]
//    ->
//           transformedVal = LAM tps. lam [[FORMALS: yb1...ybp]] xq...xN. [[REBINDS x1,yb1 ... xp,ybp]] [[FIX: body]]
//
//    [[FORMAL: SameArg xi]] -> xi
//    [[FORMAL: NewArgs vs]] -> [ [v1] ... [vN] ]                // list up individual args for Expr.Lambda
//
//    [[REBIND: xi , SameArg xi]] -> // no binding needed
//    [[REBIND: [u], NewArgs vs]] -> u = "rebuildTuple(cpi,vs)"
//    [[REBIND: us , NewArgs vs]] -> "rebuildTuple(cpi,vs)" then bind us to buildProjections. // for Expr.Lambda
//
//    rebuildTuple - create tuple based on vs fringe according to cpi tuple structure.
//
//    Note, fixup body...
//
// 5. Fixup callsites.
//
//    [[FIXCALL: APP fOrig tps args]] -> when fOrig is transformed, APP fOrig tps [[collapse args wrt cpf]]
//                                   otherwise, unchanged,  APP fOrig tps args.
//
// 6. Overview.
//    - pre-pass to find callPatterns.
//    - choose CallPattern (tuple allocs on all callsites)
//    - create replacement formals and transformedVal where needed.
//    - rw pass over expr - fixing defns and applications as required.
//    - sanity checks and done.

// Note:  ids can occur in several ways in expr at this point in compiler.
//      val id                                        - freely
//      app (val id) tys args                         - applied to tys/args (if no args, then free occurance)
//      app (reclink (val id)) tys args               - applied (recursive case)
//      app (reclink (app (val id) tys' []) tys args  - applied (recursive type instanced case)
// So, taking care counting callpatterns.
//
// Note: now considering defn projection requirements in decision.
//       no longer can assume that all call sites have explicit tuples if collapsing.
//       in these new cases, take care to have let binding sequence (eval order...)
 

// Merge a tyapp node and and app node.
let (|TyappAndApp|_|) e = 
    match e with 
    | Expr.App (f,fty,tys,args,m)       -> 
        match stripExpr f with
        | Expr.App(f2,fty2,tys2,[],m2) -> Some(f2,fty2,tys2 @ tys,args,m2)
        | Expr.App _                   -> Some(f,fty,tys,args,m) (* has args, so not combine ty args *)
        | f                             -> Some(f,fty,tys,args,m)
    | _ -> None
//-------------------------------------------------------------------------
// GetValsBoundInExpr
//-------------------------------------------------------------------------

module GlobalUsageAnalysis = begin
    let bindAccBounds vals (_isInDTree,v) =  Zset.add v vals

    let GetValsBoundInExpr expr =
       let folder = {ExprFolder0 with valBindingSiteIntercept = bindAccBounds}
       let z0 = Zset.empty valOrder
       let z  = FoldExpr folder z0 expr
       z


    //-------------------------------------------------------------------------
    // GlobalUsageAnalysis - state and ops
    //-------------------------------------------------------------------------

    type accessor = TupleGet of int * TType list

    type Results =
      (* Expr information.
       * For each v,
       *   (a) log it's usage site context = accessors // APP type-inst args
       *       where first accessor in list applies first to the v/app.
       *   (b) log it's binding site representation.
       *------
       * Future, could generalise to be graph representation of expr. (partly there).
       * This type used to be called "usage".
       *)
       { Uses     : Zmap<Val,(accessor list * TType list * Expr list) list>; (* v -> context / APP inst args *)
         Defns     : Zmap<Val,Expr>;                                        (* v -> binding repr *)
         DecisionTreeBindings    : Zset<Val>;                                              (* bound in a decision tree? *)
         RecursiveBindings  : Zmap<Val,bool * FlatVals>;                        (* v -> v list * recursive? -- the others in the mutual binding *)
         TopLevelBindings : Zset<Val>;
         IterationIsAtTopLevel      : bool
       }

    let z0 =
       { Uses     = Zmap.empty valOrder;
         Defns     = Zmap.empty valOrder;
         RecursiveBindings  = Zmap.empty valOrder;
         DecisionTreeBindings    = Zset.empty valOrder;
         TopLevelBindings = Zset.empty valOrder;
         IterationIsAtTopLevel      = true
       }

    // Note: this routine is called very frequently
    let logUse (f:Val) tup z =
       {z with Uses = 
                  match Zmap.tryFind f z.Uses with
                  | Some sites -> Zmap.add f (tup::sites) z.Uses
                  | None    -> Zmap.add f [tup] z.Uses }

    let logBinding z (isInDTree,v) =
        let z = if isInDTree then {z with DecisionTreeBindings = Zset.add v z.DecisionTreeBindings} else z
        let z = if z.IterationIsAtTopLevel then {z with TopLevelBindings = Zset.add v z.TopLevelBindings} else z
        z
        

    let logNonRecBinding z (bind:Binding) =
        (* log mubind v -> vs *)
        let v = bind.Var
        let vs = FlatList.one v
        {z with RecursiveBindings = Zmap.add v (false,vs) z.RecursiveBindings;
                Defns = Zmap.add v bind.Expr z.Defns } 

    let logRecBindings z binds =
        (* log mubind v -> vs *)
        let vs = valsOfBinds binds
        {z with RecursiveBindings = (z.RecursiveBindings,vs) ||> FlatList.fold (fun mubinds v -> Zmap.add v (true,vs) mubinds);
                Defns    = (z.Defns,binds) ||> FlatList.fold (fun eqns bind -> Zmap.add bind.Var bind.Expr eqns)  } 

    let foldUnderLambda f z x =
        let saved = z.IterationIsAtTopLevel
        let z = {z with IterationIsAtTopLevel=false}
        let z = f z x
        let z = {z with IterationIsAtTopLevel=saved}
        z

#if DEBUG
    let dumpXInfo z =
        let soAccessor (TupleGet (n,_ts)) = "#" ^ string n
        let dumpSite v (accessors,inst,args) =
            dprintf "- use %s%s %s %s\n"
              (showL (valL v))
              (match inst with
                [] -> ""
              | _  -> "@[" ^ showL (commaListL (List.map typeL inst)) ^ "]")
              (showL (spaceListL (List.map exprL args)))
              (match accessors with
                [] -> ""
              | _  -> "|> " ^ String.concat " " (List.map soAccessor accessors))
        let dumpUse v sites = List.iter (dumpSite v) sites
        let dumpTop (v:Val) = dprintf "- toplevel: %s\n" v.LogicalName
        if false then
         ( dprintf "usage:\n";
           Zmap.iter dumpUse z.Uses;
           Zset.iter dumpTop z.TopLevelBindings
          )
        else
         ()
#endif


    //-------------------------------------------------------------------------
    // GlobalUsageAnalysis - FoldExpr, foldBind collectors
    //-------------------------------------------------------------------------

    let UsageFolders g =
      // Fold expr, intercepts selected exprs.
      //   "val v"        - count []     callpattern of v
      //   "app (f,args)" - count <args> callpattern of f
      //---
      // On intercepted nodes, must continue exprF fold over any subexpressions, e.g. args.
      //------
      // Also, noting top-level bindings,
      // so must cancel top-level "foldUnderLambda" whenever step under loop/lambda:
      //   - lambdas
      //   - try/with and try/finally
      //   - for body
      //   - match targets
      //   - tmethods
      let foldLocalVal f z (vref: ValRef) = 
          if valRefInThisAssembly g.compilingFslib vref then f z vref.Deref
          else z
      let exprUsageIntercept exprF z expr =
          let rec recognise context expr = 
            match expr with
             | Expr.Val (v,_,_)                  -> 
                 // YES: count free occurance 
                 let z = foldLocalVal (fun z v -> logUse v (context,[],[]) z) z v
                 Some z
             | TyappAndApp(f,_,tys,args,_)       -> 
                 match f with
                  | Expr.Val (fOrig,_,_) ->
                    // app where function is val 
                    // YES: count instance/app (app when have term args), and then
                    //      collect from args (have intercepted this node) 
                    let collect z f = logUse f (context,tys,args) z
                    let z = foldLocalVal collect z fOrig
                    let z = List.fold exprF z args
                    Some z
                  | _ ->
                     // NO: app but function is not val 
                     None
             | Expr.Op(TOp.TupleFieldGet (n),ts,[x],_)   -> 
                 let context = TupleGet (n,ts) :: context
                 recognise context x
                 
             // lambdas end top-level status 
             | Expr.Lambda(_id,_ctorThisValOpt,_baseValOpt,_vs,body,_,_)   -> 
                 let z = foldUnderLambda exprF z body
                 Some z
             | Expr.TyLambda(_id,_tps,body,_,_) -> 
                 let z = foldUnderLambda exprF z body
                 Some z
             | _  -> 
                 None // NO: no intercept 
          
          let context = []
          recognise context expr

      let targetIntercept exprF z = function TTarget(_argvs,body,_) -> Some (foldUnderLambda exprF z body)
      let tmethodIntercept exprF z = function TObjExprMethod(_,_,_,_,e,_m) -> Some (foldUnderLambda exprF z e)
      
      {ExprFolder0 with
         exprIntercept    = exprUsageIntercept; 
         nonRecBindingsIntercept         = logNonRecBinding;
         recBindingsIntercept         = logRecBindings;
         valBindingSiteIntercept          = logBinding;
         targetIntercept  = targetIntercept;
         tmethodIntercept = tmethodIntercept;
      }


    //-------------------------------------------------------------------------
    // GlobalUsageAnalysis - entry point
    //-------------------------------------------------------------------------

    let GetUsageInfoOfImplFile g expr =
        let folder = UsageFolders g
        let z = FoldImplFile folder z0 expr
        z

end


open GlobalUsageAnalysis

//-------------------------------------------------------------------------
// misc
//-------------------------------------------------------------------------
  
let internalError str = raise(Failure(str))

let mkLocalVal m name ty topValInfo =
    let compgen    = false in (* REVIEW: review: should this be true? *)
    NewVal(name,m,None,ty,Immutable,compgen,topValInfo,taccessPublic,ValNotInRecScope,None,NormalVal,[],ValInline.Optional,XmlDoc.Empty,false,false,false,false,false,false,None,ParentNone) 

let dprintTerm header expr =
  if false then
    let str = Layout.showL (Layout.squashTo 192 (implFileL expr)) (* improve cxty! *)
    dprintf "\n\n\n%s:\n%s\n" header str
  else
    ()


//-------------------------------------------------------------------------
// TupleStructure = tuple structure
//-------------------------------------------------------------------------

type TupleStructure = (* tuple structure *)
    | UnknownTS
    | TupleTS   of TupleStructure list

let rec ValReprInfoForTS = function
    | UnknownTS  -> [ValReprInfo.unnamedTopArg]
    | TupleTS ts -> ts |> List.collect ValReprInfoForTS 

let rec andTS ts tsB =
    match ts,tsB with
    |         _   ,UnknownTS    -> UnknownTS
    | UnknownTS   ,_            -> UnknownTS
    | TupleTS ss  ,TupleTS ssB  -> if ss.Length <> ssB.Length then UnknownTS (* different tuple instances *)
                                   else TupleTS (List.map2 andTS ss ssB)

let checkTS = function
    | TupleTS []   -> internalError "exprTS: Tuple[]  not expected. (units not done that way)."
    | TupleTS [_]  -> internalError "exprTS: Tuple[x] not expected. (singleton tuples should not exist."
    | ts           -> ts   
          
let rec uncheckedExprTS = function (* explicit tuple-structure in expr *)
    | Expr.Op(TOp.Tuple,_tys,args,_) -> TupleTS (List.map uncheckedExprTS args)
    | _                               -> UnknownTS

let rec uncheckedTypeTS g ty =
    if isTupleTy g ty then 
        let tys = destTupleTy g ty 
        TupleTS (List.map (uncheckedTypeTS g) tys)
    else 
        UnknownTS

let exprTS exprs = exprs |> uncheckedExprTS |> checkTS
let typeTS g tys = tys |> uncheckedTypeTS g |> checkTS

let rebuildTS g m ts vs =
    let rec rebuild vs ts = 
      match vs,ts with
      | []   ,UnknownTS   -> internalError "rebuildTS: not enough fringe to build tuple"
      | v::vs,UnknownTS   -> vs,(exprForVal m v,v.Type)
      | vs   ,TupleTS tss -> let vs,xtys = List.fmap rebuild vs tss
                             let xs,tys  = List.unzip xtys
                             let x  = mkTupled g m xs tys
                             let ty = mkTupledTy g tys
                             vs,(x,ty)
   
    let vs,(x,_ty) = rebuild vs ts
    if vs.Length <> 0 then internalError "rebuildTS: had move fringe vars than fringe. REPORT BUG" else ();
    x

(* naive string concats, just for testing *)

/// CallPattern is tuple-structure for each argument position.
/// - callsites have a CallPattern (possibly instancing fOrig at tuple types...).
/// - the definition lambdas may imply a one-level CallPattern
/// - the definition formal projection info suggests a CallPattern
type CallPattern =
    TupleStructure list (* equality/ordering ok on this type *)
      
let callPatternOrder = (compare : CallPattern -> CallPattern -> int)
let argsCP exprs = List.map exprTS exprs
let noArgsCP = []
let isTrivialCP xs = (isNil xs)

#if DEBUG
let rec soTS = function (UnknownTS) -> "_" | TupleTS ss -> "(" ^ String.concat "," (List.map soTS ss) ^ ")"
let soCP tss = String.concat ";" (List.map soTS tss)
#endif

let rec minimalCallPattern callPattern =
    match callPattern with 
    | []                -> []
    | UnknownTS::tss    -> 
        match minimalCallPattern tss with
        | []  -> []              (* drop trailing UnknownTS *)
        | tss -> UnknownTS::tss (* non triv tss tail *)
    | (TupleTS ts)::tss -> TupleTS ts :: minimalCallPattern tss

/// INTERSECTION.
/// Combines a list of callpatterns into one common callpattern.
let commonCallPattern callPatterns =
    let rec andCPs cpA cpB =
      match cpA,cpB with
      | []       ,[]        -> []
      | tsA::tsAs,tsB::tsBs -> andTS tsA tsB :: andCPs tsAs tsBs
      | _tsA::_tsAs,[]        -> [] (* now trim to shortest - UnknownTS     :: andCPs tsAs []   *)
      | []       ,_tsB::_tsBs -> [] (* now trim to shortest - UnknownTS     :: andCPs []   tsBs *)
   
    List.reduce andCPs callPatterns

let siteCP (_accessors,_inst,args) = argsCP args
let sitesCPs sites = List.map siteCP sites

//-------------------------------------------------------------------------
// transform
//-------------------------------------------------------------------------

type TransformedFormal =
  // Indicates that
  //    - the actual arg in this position is unchanged
  //    - also menas that we keep the original formal arg
  | SameArg                          

  // Indicates 
  //    - the new formals for the transform
  //    - expr is tuple of the formals
  | NewArgs of Val list * Expr  

/// Info needed to convert f to curried form.
/// - yb1..ybp - replacement formal choices for x1...xp.
/// - transformedVal       - replaces f.
type Transform =
   { transformCallPattern : CallPattern;
     transformedFormals   : TransformedFormal list; (* REVIEW: could push these to fixup binding site *)
     transformedVal         : Val;
   }


//-------------------------------------------------------------------------
// transform - mkTransform - decided, create necessary stuff
//-------------------------------------------------------------------------

let mkTransform g (f:Val) m tps x1Ntys rty (callPattern,tyfringes: (TType list * Val list) list) =
    // Create formal choices for x1...xp under callPattern  
    let transformedFormals = 
        (callPattern,tyfringes) ||>  List.map2 (fun cpi (tyfringe,vs) -> 
            match cpi with
            | UnknownTS  -> SameArg
            | TupleTS [] -> SameArg  
            | TupleTS _ -> 
                // Try to keep the same names for the arguments if possible
                let vs = 
                    if vs.Length = tyfringe.Length then 
                        vs |> List.map (fun v -> mkCompGenLocal v.Range v.LogicalName v.Type |> fst)
                    else
                        let baseName = match vs with [v] -> v.LogicalName | _ -> "arg"
                        let baseRange = match vs with [v] -> v.Range | _ -> m
                        tyfringe |> List.mapi (fun i ty -> 
                            let name = baseName ^ string i
                            mkCompGenLocal baseRange name ty |> fst)
                        
                NewArgs (vs,rebuildTS g m cpi vs))
       
    // Create transformedVal replacement for f 
    // Mark the arity of the value 
    let topValInfo = 
        match f.ValReprInfo with 
        | None -> None 
        | _ -> Some(ValReprInfo (ValReprInfo.InferTyparInfo tps,List.collect ValReprInfoForTS callPattern,ValReprInfo.unnamedRetVal))
    (* type(transformedVal) tyfringes types replace initial arg types of f *)
    let tys1r = List.collect fst tyfringes  (* types for collapsed initial r args *)
    let tysrN = List.drop tyfringes.Length x1Ntys    (* types for remaining args *)
    let argtys = tys1r @ tysrN
    let fCty  = mkLambdaTy tps argtys rty                  
    let transformedVal  = mkLocalVal f.Range (globalNng.FreshCompilerGeneratedName (f.LogicalName,f.Range)) fCty topValInfo
  (*dprintf "mkTransform: f=%s\n"         (showL (valL f));
    dprintf "mkTransform: tps=%s\n"       (showL (commaListL (List.map typarL tps)));
    dprintf "mkTransform: callPattern=%s\n"        (soCP callPattern);
    dprintf "mkTransform: tyfringes=%s\n" (showL (commaListL (List.map (fun fr -> tupleL (List.map typeL fr)) tyfringes)));
    dprintf "mkTransform: tys1r=%s\n"     (showL (commaListL (List.map typeL tys1r)));
    dprintf "mkTransform: tysrN=%s\n"     (showL (commaListL (List.map typeL tysrN)));
    dprintf "mkTransform: rty  =%s\n"     ((showType rty));
  *)   
    { transformCallPattern = callPattern;
      transformedFormals      = transformedFormals;
      transformedVal         = transformedVal;
    }

#if DEBUG
open Microsoft.FSharp.Compiler.Layout    
let dumpTransform trans =
    dprintf " - cp   : %s\n - transformedVal   : %s\n\n"
      (soCP trans.transformCallPattern)
      (showL (valL trans.transformedVal))
#endif

//-------------------------------------------------------------------------
// transform - vTransforms - support
//-------------------------------------------------------------------------

let zipCallPatternArgTys m g (callPattern : TupleStructure list) (vss : Val list list) =
    let rec zipTSTyp ts typ =
        // match a tuple-structure and type, yields:
        //  (a) (restricted) tuple-structure, and
        //  (b) type fringe for each arg position.
        match ts with
        | TupleTS tss when isTupleTy g typ ->
            let tys = destTupleTy g typ 
            let tss,tyfringe = zipTSListTypList tss tys
            TupleTS tss,tyfringe
        | _ -> 
            UnknownTS,[typ] (* trim back CallPattern, function more general *)
    and zipTSListTypList tss tys =
        let tstys = List.map2 zipTSTyp tss tys  // assumes tss tys same length 
        let tss  = List.map fst tstys         
        let tys = List.collect snd tstys       // link fringes 
        tss,tys
    
    let vss = List.take callPattern.Length vss    // drop excessive tys if callPattern shorter 
    let tstys = List.map2 (fun ts vs -> let ts,tyfringe = zipTSTyp ts (typeOfLambdaArg m vs) in ts,(tyfringe,vs)) callPattern vss
    List.unzip tstys   

//-------------------------------------------------------------------------
// transform - vTransforms - defnSuggestedCP
//-------------------------------------------------------------------------

/// v = LAM tps. lam vs1:ty1 ... vsN:tyN. body.
/// The types suggest a tuple structure CallPattern.
/// The buildProjections of the vsi trim this down,
/// since do not want to take as components any tuple that is required (projected to).
let decideFormalSuggestedCP g z tys vss =

    let rec trimTsByAccess accessors ts =
        match ts,accessors with
        | UnknownTS ,_                       -> UnknownTS
        | TupleTS _tss,[]                     -> UnknownTS (* trim it, require the val at this point *)
        | TupleTS tss,TupleGet (i,_ty)::accessors -> 
            let tss = List.mapNth i (trimTsByAccess accessors) tss
            TupleTS tss

    let trimTsByVal z ts v =
        match Zmap.tryFind v z.Uses with
        | None       -> UnknownTS (* formal has no usage info, it is unused *)
        | Some sites -> 
            let trim ts (accessors,_inst,_args) = trimTsByAccess accessors ts
            List.fold trim ts sites

    let trimTsByFormal z ts vss = 
        match vss with 
        | [v]  -> trimTsByVal z ts v
        | vs   -> 
            let tss = match ts with TupleTS tss -> tss | _ -> internalError "trimByFormal: ts must be tuple?? PLEASE REPORT\n"
            let tss = List.map2 (trimTsByVal z) tss vs
            TupleTS tss

    let tss = List.map (typeTS g) tys (* most general TS according to type *)
    let tss = List.map2 (trimTsByFormal z) tss vss
    tss

//-------------------------------------------------------------------------
// transform - decideTransform
//-------------------------------------------------------------------------

let decideTransform g z v callPatterns (m,tps,vss:Val list list,rty) (* tys are types of outer args *) =
    let tys = List.map (typeOfLambdaArg m) vss       (* arg types *)
    (* NOTE: 'a in arg types may have been instanced at different tuples... *)
    (*       commonCallPattern has to handle those cases. *)
    let callPattern           = commonCallPattern callPatterns                   // common CallPattern 
    let callPattern           = List.take vss.Length callPattern            // restricted to max nArgs 
    (* NOW: get formal callPattern by defn usage of formals *)
    let formalCallPattern     = decideFormalSuggestedCP g z tys vss 
    let callPattern           = List.take callPattern.Length formalCallPattern
    // zip with information about known args 
    let callPattern,tyfringes = zipCallPatternArgTys m g callPattern vss
    // drop trivial tail AND 
    let callPattern           = minimalCallPattern callPattern                     
    // shorten tyfringes (zippable) 
    let tyfringes    = List.take callPattern.Length tyfringes       
  (*dprintf "decideTransform: for v=%s\n" (showL (valL v));
    List.iter (fun cp -> dprintf "- site cp    = %s\n" (soCP cp)) callPatterns;
    dprintf "- common  cp = %s\n" (soCP cp);     
    dprintf "- front   cp = %s\n" (soCP cp);
    dprintf "- arg tys    = %s\n" (showL (commaListL (List.map typeL tys)));  
    dprintf "- formalCallPattern        = %s\n" (soCP formalCallPattern);  
    dprintf "- front formalCallPattern  = %s\n" (soCP cp);
    dprintf "- zipped  cp = %s\n" (soCP cp);
    dprintf "- tyfringes  = %s\n" (showL (commaListL (List.map (List.length >> intL) tyfringes)));  
    dprintf "- minimal cp = %s\n\n" (soCP cp);
  *)   
    if isTrivialCP callPattern then
        None (* no transform *)
    else
        Some (v,mkTransform g v m tps tys rty (callPattern,tyfringes))


//-------------------------------------------------------------------------
// transform - determineTransforms
//-------------------------------------------------------------------------
      
// Public f could be used beyond assembly.
// For now, suppressing any transforms on these.
// Later, could transform f and fix up local calls and provide an f wrapper for beyond. 
let eligibleVal g (v:Val) =
    let dllImportStubOrOtherNeverInline = (v.InlineInfo = ValInline.Never)
    let mutableVal = v.IsMutable
    let byrefVal = isByrefLikeTy g v.Type
    not dllImportStubOrOtherNeverInline &&
    not byrefVal &&
    not mutableVal &&
    not v.IsMemberOrModuleBinding && //  .IsCompiledAsTopLevel &&
    not v.IsCompiledAsTopLevel 

let determineTransforms g (z : GlobalUsageAnalysis.Results) =
   let selectTransform f sites =
     if not (eligibleVal g f) then None else
     (* consider f, if it has top-level lambda (meaning has term args) *)
     match Zmap.tryFind f z.Defns with
     | None   -> None (* no binding site, so no transform *)
     | Some e -> 
        let tps,vss,_b,rty = stripTopLambda (e,f.Type)
        match List.concat vss with
        | []      -> None // defn has no term args 
        | arg1::_ -> // consider f 
          let m   = arg1.Range                       // mark of first arg, mostly for error reporting 
          let callPatterns = sitesCPs sites                   // callPatterns from sites 
          decideTransform g z f callPatterns (m,tps,vss,rty) // make transform (if required) 
  
   let vtransforms = Zmap.chooseL selectTransform z.Uses
   let vtransforms = Zmap.ofList valOrder vtransforms
   vtransforms

#if DEBUG
let dumpVTransform v tr =
    dprintf "Transform for %s\n" (showL (valL v));
    dumpTransform tr;
    stdout.Flush()
#endif


//-------------------------------------------------------------------------
// pass - penv - env of pass
//-------------------------------------------------------------------------

type penv =
   { transforms : Zmap<Val,Transform>; (* planned transforms *)
     ccu        : CcuThunk;
     g          : Env.TcGlobals;
   }

let hasTransfrom penv f = Zmap.tryFind f penv.transforms

//-------------------------------------------------------------------------
// pass - app fixup - collapseArgs
//-------------------------------------------------------------------------

(* collapseArgs:
   - the args may not be tuples (decision made on defn projection).
   - need to factor any side-effecting args out into a let binding sequence.
   - also factor buildProjections, so they share common tmps.
*)

type env = {eg : TcGlobals;
            prefix : string;
            m      : Range.range; }
let suffixE env s = {env with prefix = env.prefix ^ s}
let rangeE  env m = {env with m = m}

let push  b  bs = b::bs
let pushL xs bs = xs@bs

let newLocal  env   ty = mkCompGenLocal env.m env.prefix ty
let newLocalN env i ty = mkCompGenLocal env.m (env.prefix ^ string i) ty

let noEffectExpr env bindings x =
    match x with
    | Expr.Val (_v,_,_m) -> bindings,x
    | x                 -> 
        let tmp,xtmp = newLocal env (tyOfExpr env.eg x)
        let bind = mkCompGenBind tmp x
        push bind bindings,xtmp

// Given 'e', build 
//     let v1 = e#1
//     let v2 = e#N
let buildProjections env bindings x xtys =

    let binds,vixs = 
        xtys 
        |> List.mapi (fun i xty ->
            let vi,vix = newLocalN env i xty
            let bind = mkBind NoSequencePointAtInvisibleBinding vi (mkTupleFieldGet (x,xtys,i,env.m))
            bind,vix)
        |> List.unzip

    // Why are we reversing here? Because we end up reversing once more later
    let bindings = pushL (List.rev binds) bindings
    bindings,vixs

let rec collapseArg env bindings ts (x:Expr) =
    let m = x.Range
    let env = rangeE env m
    match ts,x with
    | UnknownTS  ,x -> 
        let bindings,vx = noEffectExpr env bindings x
        bindings,[vx]
    | TupleTS tss,Expr.Op(TOp.Tuple,_xtys,xs,_) -> 
        let env = suffixE env "'"
        collapseArgs env bindings 1 tss xs
    | TupleTS tss,x                      -> 
        // project components 
        let bindings,x = noEffectExpr env bindings x
        let env  = suffixE env "_p" 
        let xty = tyOfExpr env.eg x
        let xtys = destTupleTy env.eg xty
        let bindings,xs = buildProjections env bindings x xtys
        collapseArg env bindings (TupleTS tss) (mkTupled env.eg m xs xtys)

and collapseArgs env bindings n (callPattern) args =
    match callPattern,args with
    | []     ,args        -> bindings,args
    | ts::tss,arg::args -> 
        let env1 = suffixE env (string n)
        let bindings,xty  = collapseArg  env1 bindings ts    arg     
        let bindings,xtys = collapseArgs env  bindings (n+1) tss args
        bindings,xty @ xtys
    | _ts::_tss,[]            -> 
        internalError "collapseArgs: CallPattern longer than callsite args. REPORT BUG"


//-------------------------------------------------------------------------
// pass - app fixup
//-------------------------------------------------------------------------

// REVIEW: use mkLet etc. 
let mkLets binds (body:Expr) = 
    (binds,body) ||> List.foldBack (fun b acc -> mkLetBind acc.Range b acc) 

let fixupApp (penv:penv) (fx,fty,tys,args,m) =

    // Is it a val app, where the val has a transform? 
    match fx with
    | Expr.Val (vref,_,m) -> 
        let f = vref.Deref
        match hasTransfrom penv f with
        | Some trans -> 
            // fix it 
            let callPattern       = trans.transformCallPattern 
            let transformedVal       = trans.transformedVal         
            let fCty     = transformedVal.Type
            let fCx      = exprForVal m transformedVal
            (* [[f tps args ]] -> transformedVal tps [[COLLAPSED: args]] *)
            let env      = {prefix = "arg";m = m;eg=penv.g}
            let bindings = []
            let bindings,args = collapseArgs env bindings 0 callPattern args
            let bindings = List.rev bindings
            mkLets bindings (Expr.App (fCx,fCty,tys,args,m))
        | None       -> 
            Expr.App (fx,fty,tys,args,m) (* no change, f untransformed val *)
    | _ -> 
        Expr.App (fx,fty,tys,args,m)                      (* no change, f is expr *)


//-------------------------------------------------------------------------
// pass - mubinds - translation support
//-------------------------------------------------------------------------

let transFormal ybi xi =
    match ybi with
    | SameArg         -> [xi]                          // one arg   - where arg=vpsecs 
    | NewArgs (vs,_x) -> vs |> List.map List.singleton // many args 

let transRebind ybi xi =
    match xi,ybi with
    | _ ,SameArg        -> []                    (* no rebinding, reused original formal *)
    | [u],NewArgs (_vs,x) -> [mkCompGenBind u x]
    | us ,NewArgs (_vs,x) -> List.map2 mkCompGenBind us (tryDestTuple x)


//-------------------------------------------------------------------------
// pass - mubinds
//-------------------------------------------------------------------------

// Foreach (f,repr) where
//   If f has trans, then
//   repr = LAM tps. lam x1...xN . body
//
//   transformedVal, yb1...ybp in trans.
//
// New binding:
//
//   transformedVal = LAM tps. lam [[FORMALS: yb1 ... ybp]] xq...xN = let [[REBINDS: x1,yb1 ...]]
//                                                        body
//
// Does not fix calls/defns in binding rhs, that is done by caller.
//

let passBind penv (TBind(fOrig,repr,letSeqPtOpt) as bind) =
     let m = fOrig.Range
     match hasTransfrom penv fOrig with
     | None ->
         // fOrig no transform 
         bind
     | Some trans ->
         // fOrig has transform 
         let tps,vss,body,rty = stripTopLambda (repr,fOrig.Type) 
         // transformedVal is curried version of fOrig 
         let transformedVal    = trans.transformedVal
         // fCBody - parts - formals 
         let transformedFormals = trans.transformedFormals 
         let p     = transformedFormals.Length
         if (vss.Length < p) then internalError "passBinds: |vss|<p - detuple pass" else (); (* ASSERTION *)
         let xqNs  = List.drop p vss  
         let x1ps  = List.take p vss  
         let y1Ps  = List.concat (List.map2 transFormal transformedFormals x1ps)
         let formals = y1Ps @ xqNs
         // fCBody - parts 
         let rebinds = List.concat (List.map2 transRebind transformedFormals x1ps)
         // fCBody - rebuild 
         // fCBody = TLambda tps. Lam formals. let rebinds in body 
         let rbody,rt  = mkLetsBind            m rebinds body,rty   
         let bind      = mkMultiLambdaBind transformedVal letSeqPtOpt m tps formals (rbody,rt)
         // result 
         bind

let passBinds penv binds = binds |> FlatList.map (passBind penv) 

//-------------------------------------------------------------------------
// pass - passBindRhs
//
// At bindings (letrec/let),
//   0. run pass of bodies first.
//   1. transform bindings (as required),
//      yields new bindings and fixup data for callsites.
//   2. required to fixup any recursive calls in the bodies (beware O(n^2) cost)
//   3. run pass over following code.
//-------------------------------------------------------------------------

let passBindRhs _penv conv (TBind (v,repr,letSeqPtOpt)) = TBind(v,conv repr,letSeqPtOpt)
let preInterceptExpr (penv:penv) conv expr =
  match expr with
  | Expr.LetRec (binds,e,m,_) ->
     let binds = FlatList.map (passBindRhs penv conv) binds
     let binds = passBinds penv binds
     Some (mkLetRecBinds m binds (conv e))
  | Expr.Let (bind,e,m,_) ->  
     let bind = passBindRhs penv conv bind
     let bind = passBind penv bind
     Some (mkLetBind m bind (conv e))
  | TyappAndApp(f,fty,tys,args,m) ->
     // match app, and fixup if needed 
     let args = List.map conv args
     let f = conv f
     Some (fixupApp penv (f,fty,tys,args,m) )
  | _ -> None
  

let postTransformExpr (penv:penv) expr =
    match expr with
    | Expr.LetRec (binds,e,m,_) ->
        let binds = passBinds penv binds
        Some (mkLetRecBinds m binds e)
    | Expr.Let (bind,e,m,_) ->  
        let bind = passBind penv bind
        Some (mkLetBind m bind e)
    | TyappAndApp(f,fty,tys,args,m) ->
        // match app, and fixup if needed 
        Some (fixupApp penv (f,fty,tys,args,m) )
    | _ -> None
  

let passImplFile penv ass = 
    ass |> RewriteImplFile {PreIntercept =None (* Some (preInterceptExpr penv) *);
                            PostTransform= postTransformExpr penv (* (fun _ -> None)  *);
                            IsUnderQuotations=false } 


//-------------------------------------------------------------------------
// entry point
//-------------------------------------------------------------------------

let DetupleImplFile ccu g expr =
   // collect expr info - wanting usage contexts and bindings 
   let (z : Results) = GetUsageInfoOfImplFile g expr
   // For each Val, decide Some "transform", or None if not changing
   let vtrans = determineTransforms g z

#if DEBUG
   // Diagnostics - summary of planned transforms 
   if verbose then dprintf "note: detuple - %d functions transformed\n" (List.length (Zmap.keys vtrans));
   if verbose then Zmap.iter dumpVTransform vtrans;
#endif

   (* Pass over term, rewriting bindings and fixing up call sites, under penv *)
   let penv = {g=g; transforms = vtrans; ccu = ccu}
   if verbose then dprintTerm "DetupleAssembly before:" expr;
   if verbose then dprintf   "DetupleAssembly: pass\n";
   let expr = passImplFile penv expr
   if verbose then dprintTerm "DetupleAssembly after:" expr;
   if verbose then dprintf   "DetupleAssembly: done\n";
   expr
