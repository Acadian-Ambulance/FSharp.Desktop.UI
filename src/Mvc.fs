﻿namespace FSharp.Desktop.UI

open System
open System.ComponentModel
open System.Runtime.ExceptionServices
open System.Threading
open System.Reactive
open System.Reactive.Concurrency
open System.Reactive.Linq

open System.Windows

type IDialogService = 
    abstract MessageBox: text: string * ?caption: string * ?button: MessageBoxButton * ?icon: MessageBoxImage * ?defaultResult: MessageBoxResult * ?options: MessageBoxOptions * ?owner: Window -> MessageBoxResult
    //more specialized members can be defined like: Error, Warning, Confrim

type IPartialView<'Event, 'Model> = 
    abstract Events: IObservable<'Event> with get
    abstract SetBindings: 'Model -> unit
    abstract DialogService: IDialogService with get

type IView<'Event, 'Model> =
    inherit IPartialView<'Event, 'Model>
    abstract ShowDialog: unit -> bool
    abstract Show: unit -> Async<unit>

type EventHandler<'Model> = 
    | Sync of ('Model -> unit)
    | Async of ('Model -> Async<unit>)

type IController<'Event, 'Model> =
    abstract InitModel : 'Model -> unit
    abstract Dispatcher : ('Event -> EventHandler<'Model>)
    abstract DialogService: IDialogService with set

[<AbstractClass>]
type Controller<'Event, 'Model>() =

    let mutable dialogService = {
        new IDialogService with 
            member __.MessageBox( text, _, _, _, _, _, _) = undefined //or maybe better to use Debug.WriteLine or empty body as deafult
    }

    interface IController<'Event, 'Model> with
        member this.InitModel model = this.InitModel model
        member this.Dispatcher = this.Dispatcher
        member __.DialogService with set value = dialogService <- value

    abstract InitModel : 'Model -> unit
    abstract Dispatcher : ('Event -> EventHandler<'Model>)
    member __.DialogService = dialogService

    static member Create callback = {
        new Controller<'Event, 'Model>() with
            member this.InitModel _ = ()
            member this.Dispatcher = callback
    } 

    static member Create callback = {
        new Controller<'Event, 'Model>() with
            member this.InitModel _ = ()
            member this.Dispatcher = fun event -> Sync(callback event)
    } 

[<Sealed>]
type Mvc<'Event, 'Model when 'Model :> INotifyPropertyChanged>(model : 'Model, view : IView<'Event, 'Model>, controller : IController<'Event, 'Model>) =

    let mutable error = fun(exn, _) -> ExceptionDispatchInfo.Capture(exn).Throw()
    
    member this.Start() =
        controller.InitModel model
        view.SetBindings model

        Observer.Create(fun event -> 
            match controller.Dispatcher event with
            | Sync eventHandler ->
                try eventHandler model 
                with why -> error(why, event)
            | Async eventHandler -> 
                Async.StartWithContinuations(
                    computation = eventHandler model, 
                    continuation = ignore, 
                    exceptionContinuation = (fun why -> error(why, event)),
                    cancellationContinuation = ignore))        
#if DEBUG
        |> Observer.Checked
#endif
        |> Observer.preventReentrancy
        |> Observer.notifyOnDispatcher
        |> view.Events.Subscribe

    member this.StartDialog() =
        use subscription = this.Start()
        view.ShowDialog()

    member this.StartWindow() =
        async {
            use subscription = this.Start()
            return! view.Show()
        }

    member this.Error with get() = error and set value = error <- value

    member this.Compose(childController : IController<'EX, 'MX>, childView : IPartialView<_, _>, childModelSelector) = 
        let compositeView = {
                new IView<_, _> with
                    member __.Events = 
                        Observable.merge (Observable.map Choice1Of2 view.Events) (Observable.map Choice2Of2 childView.Events)
                    member __.SetBindings model =
                        view.SetBindings model  
                        model |> childModelSelector |> childView.SetBindings
                    member __.DialogService = view.DialogService
                    member __.Show() = view.Show()
                    member __.ShowDialog() = view.ShowDialog()
        }

        let compositeController = { 
            new Controller<_, _>() with
                member __.InitModel model = 
                    controller.InitModel model
                    model |> childModelSelector |> childController.InitModel
                member __.Dispatcher = function 
                    | Choice1Of2 e -> controller.Dispatcher e
                    | Choice2Of2 e -> 
                        match childController.Dispatcher e with
                        | Sync handler -> Sync(childModelSelector >> handler)  
                        | Async handler -> Async(childModelSelector >> handler) 
        }

        Mvc(model, compositeView, compositeController)

    static member (<+>) (mvc : Mvc<_, _>,  (childController, childView, childModelSelector)) = 
        mvc.Compose(childController, childView, childModelSelector)

    member this.Compose(childController : IController<_, _>, events : IObservable<_>) = 
        let childView = {
            new IPartialView<_, _> with
                member __.Events = events
                member __.SetBindings _ = () 
                member __.DialogService = view.DialogService
        }
        this.Compose(childController, childView, id)

        

    