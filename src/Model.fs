﻿namespace FSharp.Desktop.UI

open System
open System.ComponentModel
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Quotations

open Castle.DynamicProxy

module internal MethodInfo = 

    let (|PropertySetter|_|) (m : MethodInfo) =
        match m.Name.Split('_') with
        | [| "set"; propertyName |] -> assert m.IsSpecialName; Some propertyName
        | _ -> None

    let (|PropertyGetter|_|) (m : MethodInfo) =
        match m.Name.Split('_') with
        | [| "get"; propertyName |] -> assert m.IsSpecialName; Some propertyName
        | _ -> None

    let (|Abstract|_|) (m : MethodInfo) = if m.IsAbstract then Some() else None
    let (|Virtual|_|) (m : MethodInfo) = if m.IsVirtual then Some() else None

open MethodInfo

[<AbstractClass>]
type Model() = 

    let propertyChangedEvent = Event<_,_>()

    let errors = Dictionary()
    let errorsChanged = Event<_,_>()

    static let proxyFactory = ProxyGenerator()
    static let proxyOptions = ProxyGenerationOptions {
        new IProxyGenerationHook with
            member __.ShouldInterceptMethod(type', method') = 
                match method' with 
                | Virtual & PropertySetter propertyName when type'.GetProperty(propertyName).GetGetMethod() <> null -> true
                | Virtual & PropertyGetter propertyName when type'.GetProperty(propertyName).GetSetMethod() <> null -> true 
                | _ -> false 
            member __.NonProxyableMemberNotification(_, _) = ()          
            member __.MethodsInspected() = ()          
    }

    static member Create<'T when 'T :> Model and 'T : not struct>([<ParamArray>] constructorArguments)  = 
        let interceptors : IInterceptor[] = [| PropertyInterceptor() |]
        proxyFactory.CreateClassProxy(typeof<'T>, proxyOptions, constructorArguments, interceptors) |> unbox<'T>    

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member this.PropertyChanged = propertyChangedEvent.Publish

    member this.NotifyPropertyChanged propertyName = 
        propertyChangedEvent.Trigger(this, PropertyChangedEventArgs propertyName)

    member this.NotifyPropertyChanged(propertySelector: Expr)= 
        match propertySelector with 
        | Patterns.PropertyGet(_, property, _) -> this.NotifyPropertyChanged property.Name
        | _ -> invalidOp "Expecting property getter expression"

    interface INotifyDataErrorInfo with
        member this.HasErrors = 
            errors.Values |> Seq.collect id |> Seq.exists (not << String.IsNullOrEmpty)
        member this.GetErrors propertyName = 
            if String.IsNullOrEmpty propertyName 
            then upcast errors.Values 
            else upcast (match errors.TryGetValue propertyName with | true, errors -> errors | false, _ -> Array.empty)
        [<CLIEvent>]
        member this.ErrorsChanged = errorsChanged.Publish

    member this.SetErrors(propertyName, [<ParamArray>] messages: string[]) = 
        errors.[propertyName] <- messages
        errorsChanged.Trigger(this, DataErrorsChangedEventArgs propertyName)

and internal PropertyInterceptor() =
    let data = Dictionary()

    interface IInterceptor with
        member this.Intercept invocation = 
            match invocation.Method with 
                | Abstract & PropertySetter propertyName -> 
                    let _, prevValue = data.TryGetValue propertyName
                    data.[propertyName] <- invocation.Arguments.[0]
                    if invocation.Arguments.[0] <> prevValue
                    then 
                        let model: Model = invocation.InvocationTarget |> unbox
                        model.NotifyPropertyChanged propertyName
                        model.SetErrors(propertyName, Array.empty)

                | Abstract & PropertyGetter propertyName ->
                    match data.TryGetValue propertyName with 
                    | true, value -> invocation.ReturnValue <- value 
                    | false, _ -> 
                        let returnType = invocation.Method.ReturnType
                        if returnType.IsValueType then 
                            invocation.ReturnValue <- Activator.CreateInstance returnType
                | Virtual & PropertySetter propertyName -> 
                    let prevValue = invocation.TargetType.GetProperty(propertyName).GetValue(invocation.InvocationTarget)
                    invocation.Proceed()
                    if invocation.Arguments.[0] <> prevValue
                    then 
                        let model: Model = invocation.InvocationTarget |> unbox
                        model.NotifyPropertyChanged propertyName
                        model.SetErrors(propertyName, Array.empty)
                | _ -> 
                    invocation.Proceed()

[<RequireQualifiedAccess>]
module Mvc = 

    let inline startDialog(view, controller) = 
        let model = (^Model : (static member Create : unit -> ^Model ) ())
        if Mvc<'Events, ^Model>(model, view, controller).StartDialog() then Some model else None

    let inline startWindow(view, controller) = 
        async {
            let model = (^Model : (static member Create : unit -> ^Model) ())
            let! isOk = Mvc<'Events, ^Model>(model, view, controller).StartWindow()
            return if isOk then Some model else None
        }
