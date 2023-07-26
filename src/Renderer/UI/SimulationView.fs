//(*
//    SimulationView.fs
//
//    View for simulation in the right tab.
//*)
//
module SimulationView

open Fulma
open Fulma.Extensions.Wikiki
open Fable.React
open Fable.React.Props

open NumberHelpers
open Helpers
open TimeHelpers
open JSHelpers
open DiagramStyle
open Notifications
open PopupView
open MemoryEditorView
open ModelType
open CommonTypes
open SimulatorTypes
open Extractor
open Simulator
open Sheet.SheetInterface
open DrawModelType

open Optics
open Optics.Operators

module Constants =
    let maxArraySize = 550
    let boxMaxChars = 34

/// save verilog file
/// TODO: the simulation error display here is shared with step simulation and also waveform simulation -
/// maybe it should be a subfunction.
let verilogOutput (vType: Verilog.VMode) (model: Model) (dispatch: Msg -> Unit) =
    printfn "Verilog output"
    match FileMenuView.updateProjectFromCanvas model dispatch, model.Sheet.GetCanvasState() with
        | Some proj, state ->
            match model.UIState with  //TODO should this be its own UI operation?
            | Some _ ->
                () // do nothing if in middle of I/O operation
            | None ->
                startCircuitSimulation 2 proj.OpenFileName (state) proj.LoadedComponents
                |> (function 
                    | Ok sim -> 
                        let path = FilesIO.pathJoin [| proj.ProjectPath; proj.OpenFileName + ".v" |]
                        printfn "writing %s" proj.ProjectPath
                        try 
                            let code = (Verilog.getVerilog vType sim.FastSim Verilog.CompilationProfile.Release)
                            FilesIO.writeFile path code
                        with
                        | e -> 
                            printfn $"Error in Verilog output: {e.Message}"
                            Error e.Message
                        |> Notifications.displayAlertOnError dispatch
                        dispatch <| ChangeRightTab Simulation
                        let note = successSimulationNotification $"verilog output written to file {path}"
                        dispatch  <| SetSimulationNotification note
                    | Error simError ->
                       printfn $"Error in simulation prevents verilog output {simError.Msg}"
                       dispatch <| ChangeRightTab Simulation
                       if simError.InDependency.IsNone then
                           // Highlight the affected components and connection only if
                           // the error is in the current diagram and not in a
                           // dependency.
                           (simError.ComponentsAffected, simError.ConnectionsAffected)
                           |> SetHighlighted |> dispatch
                       Error simError
                       |> StartSimulation
                       |> dispatch)
        | _ -> () // do nothing if no project is loaded

let setFastSimInputsToDefault (fs:FastSimulation) =
    fs.FComps
    |> Map.filter (fun cid fc -> fc.AccessPath = [] && match fc.FType with | Input1 _ -> true | _ -> false)
    |> Map.map (fun cid fc -> fst cid, match fc.FType with | Input1 (w,defVal) -> (w,defVal) | _ -> failwithf "What? Impossible")
    |> Map.toList
    |> List.map (fun ( _, (cid, (w,defaultVal ))) -> 
        match w,defaultVal with
        | _, Some defaultVal -> cid, convertInt64ToFastData w (int64 defaultVal)
        | _, None -> cid, convertIntToFastData w 0u)
    |> List.iter (fun (cid, wire) -> FastRun.changeInput cid (FSInterface.IData wire) 0 fs)

let InputDefaultsEqualInputs fs (model:Model) =
    let tick = fs.ClockTick
    fs.FComps
    |> Map.filter (fun cid fc -> fc.AccessPath = [] && match fc.FType with | Input1 _ -> true | _ -> false)
    |> Map.map (fun fid fc ->
        let cid = fst fid
        if Map.containsKey cid (Optic.get SheetT.symbols_ model.Sheet) then
            let newDefault =
                if fc.OutputWidth 0 > 32 then
                    convertBigIntToInt32 fc.Outputs[0].BigIntStep[tick % fs.MaxArraySize]
                else
                    int fc.Outputs[0].UInt32Step[tick % fs.MaxArraySize]
            let typ = (Optic.get (SheetT.symbolOf_ cid) model.Sheet).Component.Type
            match typ with
            | Input1(_, Some d) -> d = newDefault
            | _ -> newDefault = 0
        else
            true)
    |> Map.values
    |> Seq.forall id

let setInputDefaultsFromInputs fs (dispatch: Msg -> Unit) =
    let setInputDefault (newDefault: int) (sym: SymbolT.Symbol) =
        let comp = sym.Component
        let comp' = 
            let ct =
                match comp.Type with 
                | Input1(w,defVal) -> Input1(w,Some newDefault)
                | x -> x
            {comp with Type = ct}
        {sym with Component = comp'}
    let tick = fs.ClockTick
    fs.FComps
    |> Map.filter (fun cid fc -> fc.AccessPath = [] && match fc.FType with | Input1 _ -> true | _ -> false)
    |> Map.map (fun fid fc ->
        let cid = fst fid
        let newDefault =
            if fc.OutputWidth 0 > 32 then
                convertBigIntToInt32 fc.Outputs[0].BigIntStep[tick % fs.MaxArraySize]
            else
                int fc.Outputs[0].UInt32Step[tick % fs.MaxArraySize]
        SymbolUpdate.updateSymbol (setInputDefault (int newDefault)) cid
        |> Optic.map DrawModelType.SheetT.symbol_
        |> Optic.map ModelType.sheet_
        |> UpdateModel
        |> dispatch)
    |> ignore


//----------------------------View level simulation helpers------------------------------------//


type SimCache = {
    Name: string
    StoredState: LoadedComponent list
    StoredResult: Result<SimulationData, SimulationError>
    }



let simCacheInit () = {
    Name = ""; 
    StoredState = []
    StoredResult = Ok {
        FastSim = 
            printfn "Creating cache"
            FastCreate.simulationPlaceholder
        Graph = Map.empty 
        Inputs = []
        Outputs = []
        IsSynchronous=false
        NumberBase = NumberBase.Hex
        ClockTickNumber = 0
        }
    }
        
/// Used to store last canvas state and its simulation
let mutable simCache: SimCache = simCacheInit ()

let cacheIsEqual (cache: SimCache) (ldcs: LoadedComponent list ) : bool=
    match cache.StoredResult with
    | Error _ -> false
    | Ok {FastSim =fs} -> 
        fs.SimulatedCanvasState
        |> List.forall (fun ldc' ->
            ldcs
            |> List.tryFind (fun ldc'' -> ldc''.Name = ldc'.Name)
            |> Option.map (loadedComponentIsEqual ldc')
            |> (=) (Some true))
            

/// Start up a simulation, doing all necessary checks and generating simulation errors
/// if necesary. The code to do this is quite long so results are memoized. 
let prepareSimulationMemoized
        (simulationArraySize: int)
        (openFileName: string)
        (diagramName : string)
        (canvasState : CanvasState)
        (loadedDependencies : LoadedComponent list)
        : Result<SimulationData, SimulationError> * CanvasState =
    //printfn $"Diagram{diagramName}, open={openFileName}, deps = {loadedDependencies |> List.map (fun dp -> dp.Name)}"
    let storedArraySize =
        match simCache.StoredResult with
        | Ok sd -> sd.FastSim.MaxArraySize
        | _ -> 0
    let ldcs = addStateToLoadedComponents openFileName canvasState loadedDependencies
    let isSame = 
            storedArraySize = simulationArraySize &&
            diagramName = simCache.Name &&
            cacheIsEqual simCache ldcs
    if  isSame then
        simCache.StoredResult, canvasState
    else
        printfn "New simulation"
        let name, state, ldcs = getStateAndDependencies diagramName ldcs
        let simResult = startCircuitSimulation simulationArraySize diagramName state ldcs 
        simCache <- {
            Name = diagramName
            StoredState = ldcs
            StoredResult = simResult
            }
        simResult, canvasState
   
let makeDummySimulationError msg = {
        Msg = msg
        ErrType = Other
        InDependency = None
        ConnectionsAffected = []
        ComponentsAffected = []
    }

let endSimulation endMsg dispatch =
    dispatch CloseSimulationNotification // Close error notifications.
    dispatch <| Sheet (SheetT.ResetSelection) // Remove highlights.
    dispatch endMsg // End simulation.
    dispatch <| (JSDiagramMsg << InferWidths) () // Repaint connections.

/// Start simulating the current Diagram.
/// Return SimulationData that can be used to extend the simulation
/// as needed, or error if simulation fails.
/// Note that simulation is only redone if current canvas changes.
let simulateModel (simulatedSheet: string option) (simulationArraySize: int) openSheetCanvasState model =
    let start = TimeHelpers.getTimeMs()
    match openSheetCanvasState, model.CurrentProj with
    | _, None -> 
        Error (makeDummySimulationError "What - Internal Simulation Error starting simulation - I don't think this can happen!"), openSheetCanvasState
    | canvasState, Some project ->
        let simSheet = Option.defaultValue project.OpenFileName simulatedSheet
        let otherComponents = 
            project.LoadedComponents 
            |> List.filter (fun comp -> comp.Name <> project.OpenFileName)
        (canvasState, otherComponents)
        ||> prepareSimulationMemoized simulationArraySize project.OpenFileName simSheet 
        |> TimeHelpers.instrumentInterval "MakeSimData" start

let changeBase dispatch numBase = numBase |> SetSimulationBase |> dispatch

/// A line that can be used for an input, an output, or a state.
let private splittedLine leftContent rightConent =
    Level.level [Level.Level.Props [Style [MarginBottom "10px"]]] [
        Level.left [] [
            Level.item [] [ leftContent ]
        ]
        Level.right [] [
            Level.item [] [ rightConent ]
        ]
    ]

/// Pretty print a label with its width.
let makeIOLabel label width =
    let label = cropToLength 15 true label
    match width with
    | 1 -> label
    | w -> sprintf "%s (%d bits)" label w

let private viewSimulationInputs
        (numberBase : NumberBase)
        (simulationData : SimulationData)
        (inputs : (SimulationIO * FSInterface) list)
        dispatch =

    let simulationGraph = simulationData.Graph
    let makeInputLine ((ComponentId inputId, ComponentLabel inputLabel, width), inputVals) =
        let valueHandle =
            match inputVals with
            | IData {Dat = (Word bit); Width =1} ->
                // For simple bits, just have a Zero/One button.
                Button.button [
                    Button.Props [ simulationBitStyle ]
                    //Button.Color IsPrimary
                    (match bit with 0u -> Button.Color Color.IsGreyLighter | _ -> Button.Color IsPrimary)
                    Button.IsHovered false
                    Button.OnClick (fun _ ->
                        let newBit = 1u - bit
                        let graph = simulationGraph
                        FastRun.changeInput (ComponentId inputId) (IData {Dat = Word newBit; Width = 1}) simulationData.ClockTickNumber simulationData.FastSim
                        dispatch <| SetSimulationGraph(graph, simulationData.FastSim)
                    )
                ] [ str <| bitToString (match bit with 0u -> Zero | _ -> One)]
            | IData bits ->
                let defValue = fastDataToPaddedString  Constants.boxMaxChars numberBase bits
                Input.text [
                    Input.Key (numberBase.ToString())
                    Input.DefaultValue defValue
                    Input.Props [
                        simulationNumberStyle
                        OnChange (getTextEventValue >> (fun text ->
                            match strToIntCheckWidth width text with
                            | Error err ->
                                let note = errorPropsNotification err
                                dispatch  <| SetSimulationNotification note
                            | Ok num ->
                                let bits = convertInt64ToFastData width num
                                // Close simulation notifications.
                                CloseSimulationNotification |> dispatch
                                // Feed input.
                                let graph = simulationGraph
                                FastRun.changeInput (ComponentId inputId) (IData bits) simulationData.ClockTickNumber simulationData.FastSim
                                dispatch <| SetSimulationGraph(graph, simulationData.FastSim)
                        ))
                    ]
                ]
            | IAlg _ -> failwithf "what? Algebra in Step Simulation (not yet implemented)"
        splittedLine (str <| makeIOLabel inputLabel width) valueHandle
    div [] <| List.map makeInputLine inputs

let private staticBitButton bit =
    Button.button [
        Button.Props [ simulationBitStyle ]
        //Button.Color IsPrimary
        (match bit with Zero -> Button.Color IsGreyLighter | One -> Button.Color IsPrimary)
        Button.IsHovered false
        Button.Disabled true
    ] [ str <| bitToString bit ]

let private staticNumberBox maxChars numBase (bits: FastData) =
    let value = fastDataToPaddedString maxChars numBase bits
    Input.text [
        Input.IsReadOnly true
        Input.Value value
        Input.Props [simulationNumberStyle]
    ]

let private viewSimulationOutputs numBase (simOutputs : (SimulationIO * FSInterface) list) =
    let makeOutputLine ((ComponentId _, ComponentLabel outputLabel, width), inputVals) =
        let valueHandle =
            match inputVals with
            | IData {Dat = Word b; Width = 1} -> staticBitButton (match b with 0u -> Zero | _ -> One)
            | IData bits -> staticNumberBox Constants.boxMaxChars numBase bits
            | IAlg _ -> failwithf "what? Algebra in Step Simulation (not yet implemented)"
        splittedLine (str <| makeIOLabel outputLabel width) valueHandle
    div [] <| List.map makeOutputLine simOutputs

let private viewViewers numBase (simViewers : ((string*string) * int * FSInterface) list) =
    let makeViewerOutputLine ((label,fullName), width, inputVals) =
        let valueHandle =
            match inputVals with
            | IData {Dat = Word b; Width = 1} -> staticBitButton (match b with 0u -> Zero | _ -> One)
            | IData bits -> staticNumberBox Constants.boxMaxChars numBase bits
            | IAlg _ -> failwithf "what? Algebra in Step Simulation (not yet implemented)"
        let addToolTip tip react = 
            div [ 
                HTMLAttr.ClassName $"{Tooltip.ClassName} has-tooltip-right"
                Tooltip.dataTooltip tip
            ] [react]
        let line = 
            str <| makeIOLabel label width
            |> (fun r -> if fullName <> "" then addToolTip fullName r else r)
        splittedLine line valueHandle
    div [] <| List.map makeViewerOutputLine simViewers

let private viewStatefulComponents step comps numBase model dispatch =
    let getWithDefault (lab:string) = if lab = "" then "no-label" else lab
    let makeStateLine ((fc,state) : FastComponent*SimulationComponentState) =
        let label = getWithDefault fc.FullName
        match state with
        | RegisterState fd when fd.Width = 1 ->
            let bit = if fd = SimulatorTypes.fastDataZero then Zero else One
            let label = sprintf "DFF: %s" <| label
            [ splittedLine (str label) (staticBitButton bit) ]
        | RegisterState bits ->
            let label = sprintf "Register: %s (%d bits)" label bits.Width
            [ splittedLine (str label) (staticNumberBox Constants.boxMaxChars numBase bits) ]
        | RamState mem ->
            let label = sprintf "RAM: %s" <| label
            let initialMem compType = match compType with RAM1 m | AsyncRAM1 m -> m | _ -> failwithf "what? viewStatefulComponents expected RAM component but got: %A" compType
            let viewDiffBtn =
                Button.button [
                    Button.Props [ simulationBitStyle ]
                    Button.Color IsPrimary
                    Button.OnClick (fun _ ->
                        openMemoryDiffViewer (initialMem fc.FType) mem model dispatch
                    )
                ] [ str "View" ]
            [ splittedLine (str label) viewDiffBtn ]
        | _ -> []
    div [] (List.collect makeStateLine comps )

let viewSimulationError (comps: Component list, conns: Connection list) (simError : SimulationError) (model: Model) endDispatch dispatch =
    let sheetDispatch sMsg = dispatch <| Sheet sMsg
    let busWireDispatch bMsg = sheetDispatch <| SheetT.Msg.Wire bMsg
    let symbolDispatch symMsg = busWireDispatch <| BusWireT.Msg.Symbol symMsg

    let tryGetComponentById compId =
        comps
        |> List.tryFind (fun comp -> comp.Id = compId)
    
    let tryGetConnectionById connId =
        conns
        |> List.tryFind (fun conn -> conn.Id = connId)
    
    let reacListOfCompsAffected =
        simError.ComponentsAffected
        |> List.collect (fun (ComponentId s) -> Option.toList (tryGetComponentById s))
        |> List.map (fun comp -> li [] [str comp.Label])

    let error =
        match simError.ErrType with
        | OutputNotConnected ->
            let addNCAndRemovePorts() =
                let addNCtoComponent comp =
                    comp.OutputPorts
                    |> List.filter (fun port ->
                        (conns
                        |> List.forall (fun conn -> conn.Source.Id <> port.Id))
                            && (match comp.Type with
                                | NbitsAdder w ->
                                    model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCout w)
                                    port.PortNumber = Some 0
                                | NbitsAdderNoCin w ->
                                    model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCinCout w)
                                    port.PortNumber = Some 0
                                | _ -> true))
                    |> List.iter (fun port ->
                        busWireDispatch <| BusWireT.AddNotConnected ((ModelHelpers.tryGetLoadedComponents model), port, {X = comp.X + comp.W * 1.3; Y = comp.Y + comp.H/2.}))
                        
                simError.ComponentsAffected
                |> List.map (fun (ComponentId compId) -> tryGetComponentById compId)
                |> List.collect Option.toList
                |> List.iter addNCtoComponent
                endSimulation endDispatch dispatch
            div [] [
                str simError.Msg
                br []
                ul [] reacListOfCompsAffected
                Button.button [
                    Button.Color IsSuccess
                    Button.OnClick (fun _ -> addNCAndRemovePorts())
                ] [ str "Add 'Not Connected' components and/or remove ports" ]
            ]
        | InputNotConnected ->
            let removeInPorts() =
                simError.ComponentsAffected
                |> List.collect (fun (ComponentId compId) -> Option.toList (tryGetComponentById compId))
                |> List.iter (fun comp ->
                    match comp.Type with
                    | NbitsAdder w -> model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCin w)
                    | NbitsAdderNoCout w -> model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCinCout w)
                    | CounterNoLoad w -> model.Sheet.ChangeCounterComp sheetDispatch (ComponentId comp.Id) (CounterNoEnableLoad w)
                    | CounterNoEnable w -> // if no input port is connected
                        comp.InputPorts
                        |> List.forall (fun port ->
                            conns
                            |> List.exists (fun conn -> conn.Target.Id = port.Id))
                        |> function
                            | false -> model.Sheet.ChangeCounterComp sheetDispatch (ComponentId comp.Id) (CounterNoEnableLoad w)
                            | true -> ()
                    | Counter w ->
                        let pNames = Symbol.portNames (comp.Type)
                        ((List.removeAt (pNames.Length - 1) pNames, 0), conns)
                        ||> List.fold (fun (inPortList, numRemoved)  conn ->
                            if conn.Target.HostId = comp.Id then
                                // List.removeAt Option. conn.Target.PortNumber
                                List.removeAt ((CanvasStateAnalyser.getPortNumFromConnPort conn.Target comp.InputPorts) - numRemoved) inPortList, numRemoved + 1
                            else
                                inPortList, numRemoved)
                        |> function
                            | ["D"; "LOAD"; "EN"], _ ->
                                model.Sheet.ChangeCounterComp sheetDispatch (ComponentId comp.Id) (CounterNoEnableLoad w)
                            | ["D"; "LOAD"], _ ->
                                model.Sheet.ChangeCounterComp sheetDispatch (ComponentId comp.Id) (CounterNoLoad w)
                            | ["D"; "EN"], _ | ["LOAD"; "EN"], _ | ["EN"], _ ->
                                model.Sheet.ChangeCounterComp sheetDispatch (ComponentId comp.Id) (CounterNoEnable w)
                            | _ -> ()
                    | _ -> ())
                endSimulation endDispatch dispatch
            div [] [
                str simError.Msg
                br []
                ul [] reacListOfCompsAffected
                Button.button [
                    Button.Color IsSuccess
                    Button.OnClick (fun _ -> removeInPorts())
                ] [str "Remove removable input ports from component properties"]
            ]
        | UnnecessaryNC ->
            let removeNCAndChangeAdderType() =
                // delete NotConnected components
                symbolDispatch <| SymbolT.DeleteSymbols (simError.ConnectionsAffected
                    |> List.collect (fun (ConnectionId connId) ->
                        match tryGetConnectionById connId with
                        | Some conn -> [ComponentId conn.Target.HostId]
                        | None -> []))
                // delete affected connections
                busWireDispatch <| BusWireT.DeleteWires simError.ConnectionsAffected

                simError.ComponentsAffected
                |> List.collect (fun (ComponentId compId) -> Option.toList (tryGetComponentById compId))
                |> List.iter (fun comp ->
                    match comp.Type with
                    | NbitsAdder w -> model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCout w)
                    | NbitsAdderNoCin w -> model.Sheet.ChangeAdderComp sheetDispatch (ComponentId comp.Id) (NbitsAdderNoCinCout w)
                    | _ -> failwithf "Unexpected adder type. Should only encounter these 2 types with this error message")
                endSimulation endDispatch dispatch

            div [] [
                str simError.Msg
                br []
                ul [] reacListOfCompsAffected
                Button.button [
                    Button.Color IsSuccess
                    Button.OnClick (fun _ -> removeNCAndChangeAdderType())
                ] [str "Remove unnecessary 'Not Connected' components"]
            ]
        | _ ->
            match simError.InDependency with
            | None ->
                div [] [
                    str simError.Msg
                    br []
                    str <| "Please fix the error and retry."
                ]
            | Some dep ->
                div [] [
                    str <| "Error found in sheet '" + dep + "' which is a dependency:"
                    br []
                    str simError.Msg
                    br []
                    str <| "Please fix the error in this sheet and retry."
                ]
    div [] [
        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Errors" ]
        error
    ]

let private simulationClockChangePopup (simData: SimulationData) (dispatch: Msg -> Unit) (dialog:PopupDialogData) =
    let step = simData.ClockTickNumber
    div [] 
        [
            h6 [] [str $"This simulation contains {simData.FastSim.FComps.Count} components"]
            (match dialog.Int with 
            | Some n when n > step -> 
                Text.p [
                    Modifiers [Modifier.TextWeight TextWeight.Bold] 
                  ] [str "Goto Tick:"]
            | _ -> Text.p [
                            Modifiers [
                                Modifier.TextWeight TextWeight.Bold
                                Modifier.TextColor IsDanger] 
                          ] [str $"The clock tick must be > {step}"])
            br []
            Input.number [
                Input.Props [AutoFocus true;Style [Width "100px"]]
                Input.DefaultValue <| sprintf "%d" step
                Input.OnChange (getIntEventValue >> Some >> SetPopupDialogInt >> dispatch)
            ]

        ]

let simulateWithTime timeOut steps simData =
    let startTime = getTimeMs()
    FastRun.runFastSimulation None (steps + simData.ClockTickNumber) simData.FastSim |> ignore
    getTimeMs() - startTime

let cmd block =
    Elmish.Cmd.OfAsyncWith.perform block

let doBatchOfMsgsAsynch (msgs: seq<Msg>) =
    msgs
    |> Seq.map Elmish.Cmd.ofMsg 
    |> Elmish.Cmd.batch
    |> ExecCmdAsynch
    |> Elmish.Cmd.ofMsg



let simulateWithProgressBar (simProg: SimulationProgress) (model:Model) =
    match model.CurrentStepSimulationStep, model.PopupDialogData.Progress with
    | Some (Ok simData), Some barData ->
        let nComps = float simData.FastSim.FComps.Count
        let oldClock = simData.FastSim.ClockTick
        let clock = min simProg.FinalClock (simProg.ClocksPerChunk + oldClock)
        let t1 = getTimeMs()
        FastRun.runFastSimulation None clock simData.FastSim |> ignore
        printfn $"clokctick after runFastSim {clock} from {oldClock} is {simData.FastSim.ClockTick}"
        let t2 = getTimeMs()
        let speed = if t2 = t1 then 0. else (float clock - float oldClock) * nComps / (t2 - t1)
        let messages =
            if clock - oldClock < simProg.ClocksPerChunk then [   
                SetSimulationGraph(simData.Graph, simData.FastSim)
                IncrementSimulationClockTick (clock - oldClock); 
                SetPopupProgress None ]
            else [
                SetSimulationGraph(simData.Graph, simData.FastSim)
                IncrementSimulationClockTick simProg.ClocksPerChunk
                UpdatePopupProgress (fun barData -> {barData with Value = clock - simProg.InitialClock; Speed = speed})
                SimulateWithProgressBar simProg ]
        model, doBatchOfMsgsAsynch messages       
    | _ -> 
        model, Elmish.Cmd.ofMsg (SetPopupProgress None)
    
    

let simulationClockChangeAction dispatch simData (dialog:PopupDialogData) =
    let clock = 
        match dialog.Int with
        | None -> failwithf "What - must have some number from dialog"
        | Some clock -> clock
    let initClock = simData.ClockTickNumber
    let steps = clock - initClock
    let numComps = simData.FastSim.FComps.Count
    let initChunk = min steps (20000/(numComps + 1))
    let initTime = getTimeMs()
    let estimatedTime = (float steps / float initChunk) * (simulateWithTime None initChunk simData + 0.0000001)
    let chunkTime = min 2000. (estimatedTime / 5.)
    let chunk = int <| float steps * chunkTime / estimatedTime
    if steps > 2*initChunk && estimatedTime > 500. then 
        printfn "test1"
        dispatch <| SetPopupProgress 
            (Some {
                Speed = float (numComps * steps) / estimatedTime
                Value=initChunk; 
                Max=steps; 
                Title= "running simulation..."
                })
        [
            SetSimulationGraph(simData.Graph, simData.FastSim)
            IncrementSimulationClockTick (initChunk)
            ClosePopup
            SimulateWithProgressBar {
                FinalClock = clock; 
                InitialClock = initChunk + initClock; 
                ClocksPerChunk = chunk 
                }
        ]
        |> Seq.map Elmish.Cmd.ofMsg 
        |> Elmish.Cmd.batch
        |> ExecCmdAsynch
        |> dispatch
    else
        FastRun.runFastSimulation None clock simData.FastSim |> ignore
        printfn $"test2 clock={clock}, clokcticknumber= {simData.ClockTickNumber}, {simData.FastSim.ClockTick}"
        [
            SetSimulationGraph(simData.Graph, simData.FastSim)
            IncrementSimulationClockTick (clock - simData.ClockTickNumber)
            ClosePopup
        ]
        |> Seq.map Elmish.Cmd.ofMsg 
        |> Elmish.Cmd.batch
        |> ExecCmdAsynch
        |> dispatch



let private viewSimulationData (step: int) (simData : SimulationData) model dispatch =
    let viewerWidthList =
        FastRun.extractViewers simData
        |> List.map (fun (_, width, _) -> width)
    let outputWidthList =
        simData.Outputs 
        |> List.map (fun (_,_,w) -> w)       
    let hasMultiBitOutputs =
        (List.append outputWidthList viewerWidthList)|> List.map ((>) 1) |> List.isEmpty |> not
    let maybeBaseSelector =
        match hasMultiBitOutputs with
        | false -> div [] []
        | true -> baseSelector simData.NumberBase (changeBase dispatch)
    let maybeClockTickBtn =
        let step = simData.ClockTickNumber
        match simData.IsSynchronous with
        | false -> div [] []
        | true ->
            div [] [
                Button.button [
                    Button.Color IsSuccess
                    Button.OnClick (fun _ ->
                        let isDisabled (dialogData:PopupDialogData) =
                            match dialogData.Int with
                            | Some n -> n <= step
                            | None -> true
                        dialogPopup 
                            "Advance Simulation"
                            (simulationClockChangePopup simData dispatch)
                            "Goto Tick"
                            (simulationClockChangeAction dispatch simData)
                            isDisabled
                            []
                            dispatch)
                        ] [ str "Goto" ]
                str " "
                str " "
                Button.button [
                    Button.Color IsSuccess
                    Button.OnClick (fun _ ->
                        if SimulationRunner.simTrace <> None then
                            printfn "*********************Incrementing clock from simulator button******************************"
                            printfn "-------------------------------------------------------------------------------------------"
                        //let graph = feedClockTick simData.Graph
                        FastRun.runFastSimulation None (simData.ClockTickNumber+1) simData.FastSim |> ignore
                        dispatch <| SetSimulationGraph(simData.Graph, simData.FastSim)                    
                        if SimulationRunner.simTrace <> None then
                            printfn "-------------------------------------------------------------------------------------------"
                            printfn "*******************************************************************************************"
                        IncrementSimulationClockTick 1 |> dispatch
                    )
                ] [ str <| sprintf "Clock Tick %d" simData.ClockTickNumber ]
            ]
    let maybeStatefulComponents() =
        let stateful = 
            FastRun.extractStatefulComponents simData.ClockTickNumber simData.FastSim
            |> Array.toList
        match List.isEmpty stateful with
        | true -> div [] []
        | false -> div [] [
            Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Stateful components" ]
            viewStatefulComponents step stateful simData.NumberBase model dispatch
        ]
    let questionIcon = str "\u003F"

    let tip tipTxt txt =
        span [
                // Style [Float FloatOptions.Left]
                HTMLAttr.ClassName $"{Tooltip.ClassName} {Tooltip.IsMultiline}"
                Tooltip.dataTooltip tipTxt
            ]
            [
                Text.span [
                    Modifiers [
                        Modifier.TextColor IsPrimary
                    ]
                    Props [
                        Style [
                            Display DisplayOptions.InlineBlock
                            Width "80px"
                            TextAlign TextAlignOptions.Center]]
            ] [str txt] ]
    div [] [
        splittedLine maybeBaseSelector maybeClockTickBtn

        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Inputs" ]
        viewSimulationInputs
            simData.NumberBase
            simData
            (FastRun.extractFastSimulationIOs simData.Inputs simData)
            dispatch


        Heading.h5 [ 
            Heading.Props [ Style [ MarginTop "15px" ] ] 
            ] [ 
                str "Outputs &" 
                tip "Add Viewer components to any sheet in the simulation" "Viewers"
            ]
        viewViewers simData.NumberBase <| List.sort (FastRun.extractViewers simData)
        viewSimulationOutputs simData.NumberBase
        <| FastRun.extractFastSimulationIOs simData.Outputs simData

        maybeStatefulComponents()
    ]

let setSimErrorFeedback (simError:SimulatorTypes.SimulationError) (model:Model) (dispatch: Msg -> Unit) =
    let sheetDispatch sMsg = dispatch (Sheet sMsg)
    let keyDispatch = SheetT.KeyPress >> sheetDispatch
    if simError.InDependency.IsNone then
        // Highlight the affected components and connection only if
        // the error is in the current diagram and not in a
        // dependency.
        let (badComps,badConns) = (simError.ComponentsAffected, simError.ConnectionsAffected)
        dispatch <| SetHighlighted (badComps,badConns)
        if not (Sheet.isAllVisible model.Sheet badConns badComps) then
            // make whole diagram visible if any of the errors are not visible
            keyDispatch <| SheetT.KeyboardMsg.CtrlW


let viewSimulation canvasState model dispatch =
    printf "Viewing Simulation"
    // let JSState = model.Diagram.GetCanvasState ()
    let startSimulation simRes =
        let model = MemoryEditorView.updateAllMemoryComps model
        simCache <- simCacheInit ()
        simulateModel None Constants.maxArraySize canvasState model
        |> function
            | Ok (simData), state -> 
                if simData.FastSim.ClockTick = 0 then 
                    setFastSimInputsToDefault simData.FastSim
                Ok simData
            | Error simError, state ->
                printfn $"ERROR:{simError}"
                setSimErrorFeedback simError model dispatch
                Error simError
        |> StartSimulation
        |> dispatch

    match model.CurrentStepSimulationStep with
    | None ->
        let simRes = simulateModel None Constants.maxArraySize canvasState model
        let isSync = match simRes with | Ok {IsSynchronous=true},_ -> true | _ -> false
        let buttonColor, buttonText = 
            match simRes with
            | Ok _, _ -> IsSuccess, "Start Simulation"
            | Error _, _ -> IsWarning, "See Problems"
        div [] [
            str "Simulate simple logic using this tab."
            br []
            str (if isSync then "You can also use the Wave Simulation tab to view waveforms" else "")
            br []; br []
            Button.button
                [ 
                    Button.Color buttonColor; 
                    Button.OnClick (fun _ -> startSimulation simRes) ; 
                ]
                [ str buttonText ]
        ]
    | Some sim ->
        let body = match sim with
                   | Error simError -> viewSimulationError canvasState simError model EndSimulation dispatch
                   | Ok simData -> viewSimulationData simData.ClockTickNumber simData model dispatch
        let setDefaultButton =
            match sim with
            | Error _ -> div [] []
            | Ok simData ->
            div [] [
                Button.button
                    [ 
                        Button.Color IsInfo; 
                        Button.Disabled (InputDefaultsEqualInputs simData.FastSim model)
                        Button.OnClick (fun _ -> setInputDefaultsFromInputs simData.FastSim dispatch) ; 
                        Button.Props [Style [Display DisplayOptions.Inline; Float FloatOptions.Right ]]
                    ]
                    [ str "Save current input values as default" ]
            ]
        
        div [Style [Height "100%"]] [
            Button.button
                [ Button.Color IsDanger; Button.OnClick (fun _ -> endSimulation EndSimulation dispatch) ; Button.Props [Style [Display DisplayOptions.Inline; Float FloatOptions.Left ]]]
                [ str "End simulation" ]
            setDefaultButton
            br []; br []
            str "The simulation uses the diagram as it was at the moment of
                 pressing the \"Start simulation\" button."
            hr []
            body
        ]
