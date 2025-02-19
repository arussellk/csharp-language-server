module CSharpLanguageServer.Types

open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types

type ServerSettings =
    { SolutionPath: string option
      LogLevel: string }

    static member Default: ServerSettings =
        { SolutionPath = None
          LogLevel = "log" }

type CSharpMetadataInformation =
    { ProjectName: string
      AssemblyName: string
      SymbolName: string
      Source: string }

type CSharpMetadataParams =
    { TextDocument: TextDocumentIdentifier }

type CSharpMetadataResponse = CSharpMetadataInformation

[<Interface>]
type ICSharpLspServer =
    inherit ILspServer
    abstract CSharpMetadata: CSharpMetadataParams -> AsyncLspResult<CSharpMetadataResponse option>

[<Interface>]
type ICSharpLspClient =
    inherit ILspClient
    // Use a ClientCapabilitiesDTO instead of ClientCapabilities to avoid Option.map & Option.bind?
    // But ClientCapabilities is a complex type, write it again will be a huge work.
    abstract member Capabilities: ClientCapabilities option with get, set
