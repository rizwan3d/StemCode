from __future__ import annotations

import os
import platform
import sys
from dataclasses import dataclass
from importlib.resources import files
from pathlib import Path
from typing import Any, Callable

_loaded = False
_assembly_resolvers: list[Any] = []
_native_library_handles: list[Any] = []


class DotNetLoadError(RuntimeError):
    """Raised when the StemCode .NET assembly cannot be loaded."""


def load_stemcode(runtime_path: str | os.PathLike[str] | None = None) -> None:
    """Load the StemCode .NET assembly through pythonnet.

    The runtime path may point either to a directory containing StemCode.dll or
    directly to StemCode.dll. If omitted, STEMCODE_DOTNET_PATH is used.
    """

    global _loaded
    if _loaded:
        return

    path_value = runtime_path or os.getenv("STEMCODE_DOTNET_PATH")
    if path_value:
        root = Path(path_value).expanduser().resolve()
    else:
        root = Path(str(files("stemcode").joinpath("_dotnet"))).resolve()

    assembly_path = root if root.name.lower() == "stemcode.dll" else root / "StemCode.dll"
    if not assembly_path.exists():
        raise DotNetLoadError(
            f"StemCode.dll was not found at {assembly_path}. Reinstall the "
            "package or set STEMCODE_DOTNET_PATH to a published StemCode SDK folder."
        )

    assembly_dir = str(assembly_path.parent)
    if assembly_dir not in sys.path:
        sys.path.insert(0, assembly_dir)

    native_dirs = _register_native_library_paths(assembly_path.parent)

    try:
        from pythonnet import load

        try:
            load("coreclr")
        except RuntimeError as exc:
            if "already" not in str(exc).lower():
                raise

        import clr  # type: ignore

        _register_assembly_resolver(assembly_path.parent, native_dirs)
        clr.AddReference(str(assembly_path))
    except Exception as exc:  # pragma: no cover - depends on local .NET runtime
        raise DotNetLoadError(
            "Failed to load StemCode through pythonnet. Verify that pythonnet "
            "is installed and that a compatible .NET runtime is available."
        ) from exc

    _loaded = True


def _register_native_library_paths(assembly_dir: Path) -> tuple[Path, ...]:
    native_dirs = [
        assembly_dir,
        *(path for path in _candidate_native_dirs(assembly_dir) if path.exists()),
    ]

    if sys.platform == "win32":
        for directory in native_dirs:
            if hasattr(os, "add_dll_directory"):
                _native_library_handles.append(os.add_dll_directory(str(directory)))

        path_value = os.environ.get("PATH", "")
        paths = [str(directory) for directory in native_dirs]
        os.environ["PATH"] = os.pathsep.join([*paths, path_value]) if path_value else os.pathsep.join(paths)
    else:
        variable = "DYLD_LIBRARY_PATH" if sys.platform == "darwin" else "LD_LIBRARY_PATH"
        path_value = os.environ.get(variable, "")
        paths = [str(directory) for directory in native_dirs]
        os.environ[variable] = os.pathsep.join([*paths, path_value]) if path_value else os.pathsep.join(paths)

    return tuple(native_dirs)


def _candidate_native_dirs(assembly_dir: Path) -> tuple[Path, ...]:
    rid = _runtime_identifier()
    paths = []
    if rid:
        paths.append(assembly_dir / "runtimes" / rid / "native")

    if sys.platform == "win32":
        paths.append(assembly_dir / "runtimes" / "win" / "native")

    return tuple(paths)


def _runtime_identifier() -> str | None:
    system = platform.system().lower()
    machine = platform.machine().lower()

    architecture = {
        "amd64": "x64",
        "x86_64": "x64",
        "arm64": "arm64",
        "aarch64": "arm64",
    }.get(machine)
    if architecture is None:
        return None

    if system == "windows":
        return f"win-{architecture}"
    if system == "linux":
        return f"linux-{architecture}"
    if system == "darwin":
        return f"osx-{architecture}"

    return None


def _register_assembly_resolver(assembly_dir: Path, native_dirs: tuple[Path, ...]) -> None:
    from System import Func, IntPtr, String  # type: ignore
    from System.Reflection import Assembly, AssemblyName  # type: ignore
    from System.Runtime.Loader import AssemblyLoadContext  # type: ignore
    from System.Runtime.InteropServices import NativeLibrary  # type: ignore

    def resolve(context: Any, assembly_name: Any) -> Any:
        candidate = assembly_dir / f"{assembly_name.Name}.dll"
        if candidate.exists():
            return Assembly.LoadFrom(str(candidate))
        return None

    def resolve_unmanaged(assembly: Any, library_name: str) -> Any:
        for candidate in _native_library_candidates(native_dirs, str(library_name)):
            if candidate.exists():
                return NativeLibrary.Load(str(candidate))

        return IntPtr.Zero

    resolver = Func[AssemblyLoadContext, AssemblyName, Assembly](resolve)
    unmanaged_resolver = Func[Assembly, String, IntPtr](resolve_unmanaged)
    AssemblyLoadContext.Default.Resolving += resolver
    AssemblyLoadContext.Default.ResolvingUnmanagedDll += unmanaged_resolver
    _assembly_resolvers.append(resolver)
    _assembly_resolvers.append(unmanaged_resolver)


def _native_library_candidates(native_dirs: tuple[Path, ...], library_name: str) -> tuple[Path, ...]:
    names = {library_name}
    suffixes = (".dll", ".so", ".dylib")

    if not any(library_name.endswith(suffix) for suffix in suffixes):
        names.update(
            {
                f"{library_name}.dll",
                f"{library_name}.so",
                f"lib{library_name}.so",
                f"{library_name}.dylib",
                f"lib{library_name}.dylib",
            }
        )

    return tuple(directory / name for directory in native_dirs for name in names)


@dataclass(frozen=True)
class StemCodeSession:
    session_id: str
    provider_name: str
    model_id: str
    agent_profile_name: str
    thinking_mode: str
    reasoning_effort: str | None
    show_thinking: bool
    section_title: str
    is_resumed_section: bool
    available_model_ids: tuple[str, ...]

    @classmethod
    def from_dotnet(cls, session: Any) -> "StemCodeSession":
        return cls(
            session_id=str(session.SessionId),
            provider_name=str(session.ProviderName),
            model_id=str(session.ModelId),
            agent_profile_name=str(session.AgentProfileName),
            thinking_mode=str(session.ThinkingMode),
            reasoning_effort=_none_or_str(session.ReasoningEffort),
            show_thinking=bool(session.ShowThinking),
            section_title=str(session.SectionTitle),
            is_resumed_section=bool(session.IsResumedSection),
            available_model_ids=tuple(str(item) for item in session.AvailableModelIds),
        )


@dataclass(frozen=True)
class StemCodeResult:
    kind: str
    response_text: str
    reasoning_text: str | None
    dotnet_result: Any

    @classmethod
    def from_dotnet(cls, result: Any) -> "StemCodeResult":
        return cls(
            kind=str(result.Kind),
            response_text=str(result.ResponseText),
            reasoning_text=_none_or_str(result.ReasoningText),
            dotnet_result=result,
        )


class StemCodeClientBuilder:
    """Pythonic facade over StemCode.Sdk.StemCodeClientBuilder."""

    def __init__(self, runtime_path: str | os.PathLike[str] | None = None) -> None:
        load_stemcode(runtime_path)
        from StemCode.Sdk import StemCodeClient as DotNetStemCodeClient  # type: ignore

        self._builder = DotNetStemCodeClient.CreateBuilder()

    def use_anthropic(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseAnthropic(api_key, model)
        return self

    def use_openai(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseOpenAi(api_key, model)
        return self

    def use_google_ai_studio(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseGoogleAiStudio(api_key, model)
        return self

    def use_openrouter(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseOpenRouter(api_key, model)
        return self

    def use_deepseek(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseDeepSeek(api_key, model)
        return self

    def use_groq(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseGroq(api_key, model)
        return self

    def use_cerebras(self, api_key: str, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseCerebras(api_key, model)
        return self

    def use_ollama(self, base_url: str | None = None, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseOllama(base_url, model)
        return self

    def use_lm_studio(self, base_url: str | None = None, model: str | None = None) -> "StemCodeClientBuilder":
        self._builder.UseLmStudio(base_url, model)
        return self

    def use_openai_compatible(
        self,
        base_url: str,
        api_key: str,
        model: str | None = None,
    ) -> "StemCodeClientBuilder":
        self._builder.UseOpenAiCompatible(base_url, api_key, model)
        return self

    def with_model(self, model_id: str) -> "StemCodeClientBuilder":
        self._builder.WithModel(model_id)
        return self

    def with_workspace(self, workspace_path: str | os.PathLike[str]) -> "StemCodeClientBuilder":
        self._builder.WithWorkspace(str(workspace_path))
        return self

    def with_profile(self, profile_name: str) -> "StemCodeClientBuilder":
        self._builder.WithProfile(profile_name)
        return self

    def use_build_tool(self) -> "StemCodeClientBuilder":
        self._builder.UseBuildTool()
        return self

    def with_thinking_mode(self, thinking_mode: str) -> "StemCodeClientBuilder":
        self._builder.WithThinkingMode(thinking_mode)
        return self

    def resume_session(self, section_id: str) -> "StemCodeClientBuilder":
        self._builder.ResumeSession(section_id)
        return self

    def with_system_prompt(self, system_prompt: str) -> "StemCodeClientBuilder":
        self._builder.WithSystemPrompt(system_prompt)
        return self

    def use_stemcode_system_prompt(self) -> "StemCodeClientBuilder":
        self._builder.UseStemCodeSystemPrompt()
        return self

    def without_system_prompt(self) -> "StemCodeClientBuilder":
        self._builder.WithoutSystemPrompt()
        return self

    def auto_approve_tools(self) -> "StemCodeClientBuilder":
        self._builder.AutoApproveTools()
        return self

    def build(self) -> "StemCodeClient":
        return StemCodeClient(self._builder.Build())


class StemCodeClient:
    """Synchronous convenience wrapper around StemCode.Sdk.StemCodeClient."""

    def __init__(self, dotnet_client: Any) -> None:
        self._client = dotnet_client
        self._event_handlers: list[Any] = []

    @staticmethod
    def builder(runtime_path: str | os.PathLike[str] | None = None) -> StemCodeClientBuilder:
        return StemCodeClientBuilder(runtime_path)

    def __enter__(self) -> "StemCodeClient":
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.close()

    def initialize(self) -> StemCodeSession:
        return StemCodeSession.from_dotnet(_wait(self._client.InitializeAsync()))

    def run_turn(self, prompt: str) -> StemCodeResult:
        return StemCodeResult.from_dotnet(_wait(self._client.RunTurnAsync(prompt)))

    def run_command(self, command_text: str) -> Any:
        return _wait(self._client.RunCommandAsync(command_text))

    def close(self) -> None:
        value_task = self._client.DisposeAsync()
        if hasattr(value_task, "AsTask"):
            _wait(value_task.AsTask())
        elif hasattr(value_task, "GetAwaiter"):
            value_task.GetAwaiter().GetResult()

    def on_reasoning(self, callback: Callable[[str], Any]) -> "StemCodeClient":
        from StemCode.Sdk.Events import AssistantReasoningEventArgs  # type: ignore

        def handler(sender: Any, args: Any) -> None:
            callback(str(args.ReasoningText))

        return self._add_event_handler(
            "ReasoningReceived",
            AssistantReasoningEventArgs,
            handler,
        )

    def on_assistant_message_chunk(
        self,
        callback: Callable[..., Any],
        *callback_args: Any,
        **callback_kwargs: Any,
    ) -> "StemCodeClient":
        from StemCode.Sdk.Events import AssistantMessageChunkEventArgs  # type: ignore

        def handler(sender: Any, args: Any) -> None:
            callback(str(args.Text), *callback_args, **callback_kwargs)

        return self._add_event_handler(
            "AssistantMessageChunkReceived",
            AssistantMessageChunkEventArgs,
            handler,
        )

    def on_status(self, callback: Callable[[str, str], Any]) -> "StemCodeClient":
        from StemCode.Sdk.Events import StatusMessageEventArgs  # type: ignore

        def handler(sender: Any, args: Any) -> None:
            callback(str(args.Severity), str(args.Message))

        return self._add_event_handler("StatusMessage", StatusMessageEventArgs, handler)

    def on_tool_calls_started(self, callback: Callable[[Any], Any]) -> "StemCodeClient":
        from StemCode.Sdk.Events import ToolCallsEventArgs  # type: ignore

        def handler(sender: Any, args: Any) -> None:
            callback(args.ToolCalls)

        return self._add_event_handler("ToolCallsStarted", ToolCallsEventArgs, handler)

    def on_tool_results(self, callback: Callable[[Any], Any]) -> "StemCodeClient":
        from StemCode.Sdk.Events import ToolResultsEventArgs  # type: ignore

        def handler(sender: Any, args: Any) -> None:
            callback(args.Results)

        return self._add_event_handler("ToolResultsReceived", ToolResultsEventArgs, handler)

    def _add_event_handler(
        self,
        event_name: str,
        event_args_type: Any,
        handler: Callable[[Any, Any], None],
    ) -> "StemCodeClient":
        from System import EventHandler  # type: ignore

        typed_handler = EventHandler[event_args_type](handler)
        event = getattr(self._client, event_name)
        event += typed_handler
        self._event_handlers.append(typed_handler)
        return self


def _wait(task: Any) -> Any:
    return task.GetAwaiter().GetResult()


def _none_or_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
