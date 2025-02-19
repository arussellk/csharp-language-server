namespace CSharpLanguageServer.Handlers

open System
open System.IO

open System.Reflection
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.Build.Locator
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.Server

open CSharpLanguageServer
open CSharpLanguageServer.State
open CSharpLanguageServer.Types
open CSharpLanguageServer.Logging

[<RequireQualifiedAccess>]
module Initialization =
    let private logger = LogProvider.getLoggerByName "Initialization"

    let handleInitialize (setupTimer: unit -> unit)
                         (serverCapabilities: ServerCapabilities)
                         (scope: ServerRequestScope)
                         (p: InitializeParams)
            : Async<LspResult<InitializeResult>> = async {
        let serverName = "csharp-ls"
        logger.info (
            Log.setMessage "initializing, {name} version {version}"
            >> Log.addContext "name" serverName
            >> Log.addContext "version" (Assembly.GetExecutingAssembly().GetName().Version)
        )
        logger.info (
            Log.setMessage "{name} is released under MIT license and is not affiliated with Microsoft Corp.; see https://github.com/razzmatazz/csharp-language-server"
            >> Log.addContext "name" serverName
        )

        let vsInstanceQueryOpt = VisualStudioInstanceQueryOptions.Default
        let vsInstanceList = MSBuildLocator.QueryVisualStudioInstances(vsInstanceQueryOpt)
        if Seq.isEmpty vsInstanceList then
            raise (InvalidOperationException("No instances of MSBuild could be detected." + Environment.NewLine + "Try calling RegisterInstance or RegisterMSBuildPath to manually register one."))

        // do! infoMessage "MSBuildLocator instances found:"
        //
        // for vsInstance in vsInstanceList do
        //     do! infoMessage (sprintf "- SDK=\"%s\", Version=%s, MSBuildPath=\"%s\", DiscoveryType=%s"
        //                              vsInstance.Name
        //                              (string vsInstance.Version)
        //                              vsInstance.MSBuildPath
        //                              (string vsInstance.DiscoveryType))

        let vsInstance = vsInstanceList |> Seq.head

        logger.info(
            Log.setMessage "MSBuildLocator: will register \"{vsInstanceName}\", Version={vsInstanceVersion} as default instance"
            >> Log.addContext "vsInstanceName" vsInstance.Name
            >> Log.addContext "vsInstanceVersion" (string vsInstance.Version)
        )

        MSBuildLocator.RegisterInstance(vsInstance)

        scope.Emit(ClientCapabilityChange p.Capabilities)

        // TODO use p.RootUri
        let rootPath = Directory.GetCurrentDirectory()
        scope.Emit(RootPathChange rootPath)

        // setup timer so actors get period ticks
        setupTimer()

        // TODO: Report server info to client (name, version)
        let initializeResult =
            { InitializeResult.Default with
                Capabilities = serverCapabilities }

        return initializeResult |> LspResult.success
    }

    let handleInitialized (lspClient: ILspClient)
                          (stateActor: MailboxProcessor<ServerStateEvent>)
                          (scope: ServerRequestScope)
                          (_p: InitializedParams)
            : Async<LspResult<unit>> =
        async {
            logger.trace (
                Log.setMessage "handleInitialized: \"initialized\" notification received from client"
            )

            //
            // registering w/client for didChangeWatchedFiles notifications"
            //
            let clientSupportsWorkspaceDidChangeWatchedFilesDynamicReg =
                scope.ClientCapabilities
                |> Option.bind (fun x -> x.Workspace)
                |> Option.bind (fun x -> x.DidChangeWatchedFiles)
                |> Option.bind (fun x -> x.DynamicRegistration)
                |> Option.defaultValue true

            match clientSupportsWorkspaceDidChangeWatchedFilesDynamicReg with
            | true ->
                let fileChangeWatcher = { GlobPattern = U2.First "**/*.{cs,csproj,sln}"
                                          Kind = None }

                let didChangeWatchedFilesRegistration: Registration =
                    { Id = "id:workspace/didChangeWatchedFiles"
                      Method = "workspace/didChangeWatchedFiles"
                      RegisterOptions = { Watchers = [| fileChangeWatcher |] } |> serialize |> Some
                    }

                try
                    let! regResult =
                        lspClient.ClientRegisterCapability(
                            { Registrations = [| didChangeWatchedFilesRegistration |] })

                    match regResult with
                    | Ok _ -> ()
                    | Error error ->
                        logger.warn(
                            Log.setMessage "handleInitialized: didChangeWatchedFiles registration has failed with {error}"
                            >> Log.addContext "error" (string error)
                        )
                with
                | ex ->
                    logger.warn(
                        Log.setMessage "handleInitialized: didChangeWatchedFiles registration has failed with {error}"
                        >> Log.addContext "error" (string ex)
                    )
            | false -> ()

            //
            // retrieve csharp settings
            //
            try
                let! workspaceCSharpConfig =
                    lspClient.WorkspaceConfiguration(
                        { items=[| { Section=Some "csharp"; ScopeUri=None } |] })

                let csharpConfigTokensMaybe =
                    match workspaceCSharpConfig with
                    | Ok ts -> Some ts
                    | _ -> None

                let newSettingsMaybe =
                  match csharpConfigTokensMaybe with
                  | Some [| t |] ->
                      let csharpSettingsMaybe = t |> deserialize<ServerSettingsCSharpDto option>

                      match csharpSettingsMaybe with
                      | Some csharpSettings ->

                          match csharpSettings.solution with
                          | Some solutionPath-> Some { scope.State.Settings with SolutionPath = Some solutionPath }
                          | _ -> None

                      | _ -> None
                  | _ -> None

                // do! logMessage (sprintf "handleInitialized: newSettingsMaybe=%s" (string newSettingsMaybe))

                match newSettingsMaybe with
                | Some newSettings ->
                    scope.Emit(SettingsChange newSettings)
                | _ -> ()
            with
            | ex ->
                logger.warn(
                    Log.setMessage "handleInitialized: could not retrieve `csharp` workspace configuration section: {error}"
                    >> Log.addContext "error" (ex |> string)
                )

            //
            // start loading the solution
            //
            stateActor.Post(SolutionReloadRequest (TimeSpan.FromMilliseconds(100)))

            logger.trace(
                Log.setMessage "handleInitialized: OK")

            return LspResult.Ok()
        }
