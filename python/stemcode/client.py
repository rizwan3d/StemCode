from __future__ import annotations

import os
import platform
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass
from importlib.metadata import PackageNotFoundError, version as package_version
from importlib.resources import files
from pathlib import Path
from typing import Any, Callable
from xml.etree import ElementTree

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
    if not assembly_path.exists() and not path_value:
        root = _ensure_dotnet_runtime_from_nuget()
        assembly_path = root / "StemCode.dll"

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


def _ensure_dotnet_runtime_from_nuget() -> Path:
    package_id = os.getenv("STEMCODE_NUGET_PACKAGE", "StemCode")
    package_version_value = os.getenv("STEMCODE_NUGET_VERSION") or _python_package_version()
    source_url = os.getenv("STEMCODE_NUGET_SOURCE", "https://api.nuget.org/v3-flatcontainer")
    rid = _runtime_identifier() or "any"

    target_dir = _nuget_cache_root() / package_id.lower() / package_version_value / rid
    assembly_path = target_dir / "StemCode.dll"
    if assembly_path.exists():
        _trace_nuget(f"using cached runtime: {target_dir}")
        return target_dir

    target_dir.parent.mkdir(parents=True, exist_ok=True)

    try:
        _trace_nuget(f"downloading runtime package: {package_id} {package_version_value}")
        with tempfile.TemporaryDirectory(prefix="stemcode-nuget-") as temp_dir_value:
            temp_dir = Path(temp_dir_value)
            package_path = temp_dir / f"{package_id}.{package_version_value}.nupkg"
            _download_nuget_package(package_id, package_version_value, source_url, package_path)

            extract_dir = temp_dir / "package"
            _extract_zip_safe(package_path, extract_dir)

            runtime_dir = _select_nuget_runtime_dir(extract_dir, rid)
            if target_dir.exists():
                shutil.rmtree(target_dir)

            shutil.copytree(runtime_dir, target_dir)
            _trace_nuget(f"hydrating dependencies into: {target_dir}")
            _hydrate_nuget_dependencies(
                extract_dir,
                target_dir,
                rid,
                source_url,
                visited={(package_id.lower(), package_version_value.lower())},
            )
    except Exception as exc:
        raise DotNetLoadError(
            "StemCode.dll was not bundled and could not be downloaded from NuGet. "
            "Set STEMCODE_DOTNET_PATH to a published StemCode SDK folder, or set "
            "STEMCODE_NUGET_PACKAGE/STEMCODE_NUGET_VERSION/STEMCODE_NUGET_SOURCE "
            "to a NuGet package that contains StemCode.dll and its dependency DLLs."
        ) from exc

    if not assembly_path.exists():
        raise DotNetLoadError(
            f"The NuGet package {package_id} {package_version_value} did not provide "
            "StemCode.dll in a supported layout."
        )

    return target_dir


def _python_package_version() -> str:
    try:
        return package_version("stemcode-sdk")
    except PackageNotFoundError:
        return "0.1.0"


def _nuget_cache_root() -> Path:
    configured = os.getenv("STEMCODE_DOTNET_CACHE")
    if configured:
        return Path(configured).expanduser().resolve()

    if sys.platform == "win32":
        root = os.getenv("LOCALAPPDATA")
        if root:
            return Path(root) / "StemCode" / "python-dotnet"

    if sys.platform == "darwin":
        return Path.home() / "Library" / "Caches" / "StemCode" / "python-dotnet"

    root = os.getenv("XDG_CACHE_HOME")
    if root:
        return Path(root) / "stemcode" / "python-dotnet"

    return Path.home() / ".cache" / "stemcode" / "python-dotnet"


def _download_nuget_package(package_id: str, version: str, source_url: str, destination: Path) -> None:
    normalized_source = source_url.rstrip("/")
    lower_id = package_id.lower()
    url = f"{normalized_source}/{lower_id}/{version.lower()}/{lower_id}.{version.lower()}.nupkg"
    _trace_nuget(f"GET {package_id} {version}")

    request = urllib.request.Request(url, headers={"User-Agent": "stemcode-sdk-python"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            destination.write_bytes(response.read())
    except urllib.error.HTTPError as exc:
        raise DotNetLoadError(f"NuGet package download failed with HTTP {exc.code}: {url}") from exc


def _hydrate_nuget_dependencies(
    extract_dir: Path,
    target_dir: Path,
    rid: str,
    source_url: str,
    visited: set[tuple[str, str]],
) -> None:
    for dependency_id, dependency_version in _read_nuget_dependencies(extract_dir):
        normalized_version = _normalize_nuget_version(dependency_version)
        key = (dependency_id.lower(), normalized_version.lower())
        if key in visited:
            continue

        visited.add(key)
        _trace_nuget(f"dependency: {dependency_id} {normalized_version}")

        with tempfile.TemporaryDirectory(prefix="stemcode-nuget-dep-") as temp_dir_value:
            temp_dir = Path(temp_dir_value)
            package_path = temp_dir / f"{dependency_id}.{normalized_version}.nupkg"
            _download_nuget_package(dependency_id, normalized_version, source_url, package_path)

            dependency_extract_dir = temp_dir / "package"
            _extract_zip_safe(package_path, dependency_extract_dir)

            _copy_nuget_managed_assets(dependency_extract_dir, target_dir)
            _copy_nuget_runtime_assets(dependency_extract_dir, target_dir, rid)
            if _should_hydrate_dependency_dependencies(dependency_id):
                _hydrate_nuget_dependencies(dependency_extract_dir, target_dir, rid, source_url, visited)


def _read_nuget_dependencies(extract_dir: Path) -> tuple[tuple[str, str], ...]:
    nuspec_files = sorted(extract_dir.glob("*.nuspec"))
    if not nuspec_files:
        return ()

    root = ElementTree.parse(nuspec_files[0]).getroot()
    namespace = ""
    if root.tag.startswith("{"):
        namespace = root.tag.split("}", 1)[0] + "}"

    dependencies = []
    for dependency in root.findall(f".//{namespace}dependency"):
        dependency_id = dependency.attrib.get("id")
        dependency_version = dependency.attrib.get("version")
        if dependency_id and dependency_version:
            dependencies.append((dependency_id, dependency_version))

    return tuple(dependencies)


def _normalize_nuget_version(version: str) -> str:
    normalized = version.strip()
    if "," in normalized:
        normalized = normalized.strip("[]()").split(",", 1)[0].strip()
    return normalized.strip("[]()")


def _copy_nuget_managed_assets(extract_dir: Path, target_dir: Path) -> None:
    asset_dir = _select_framework_asset_dir(extract_dir)
    if asset_dir is None:
        return

    for asset in asset_dir.iterdir():
        if asset.is_file() and asset.suffix.lower() in {".dll", ".json"}:
            shutil.copy2(asset, target_dir / asset.name)


def _select_framework_asset_dir(extract_dir: Path) -> Path | None:
    candidates = (
        extract_dir / "lib" / "net10.0",
        extract_dir / "lib" / "net9.0",
        extract_dir / "lib" / "net8.0",
        extract_dir / "lib" / "net7.0",
        extract_dir / "lib" / "net6.0",
        extract_dir / "lib" / "netstandard2.1",
        extract_dir / "lib" / "netstandard2.0",
    )

    for candidate in candidates:
        if candidate.exists():
            return candidate

    lib_dir = extract_dir / "lib"
    if not lib_dir.exists():
        return None

    framework_dirs = sorted(path for path in lib_dir.iterdir() if path.is_dir())
    return framework_dirs[-1] if framework_dirs else None


def _copy_nuget_runtime_assets(extract_dir: Path, target_dir: Path, rid: str) -> None:
    runtime_candidates = (
        extract_dir / "runtimes" / rid / "native",
        extract_dir / "runtimes" / _rid_family(rid) / "native",
    )
    for candidate in runtime_candidates:
        if candidate.exists():
            native_target = target_dir / "runtimes" / candidate.parent.name / "native"
            native_target.mkdir(parents=True, exist_ok=True)
            for asset in candidate.iterdir():
                if asset.is_file():
                    shutil.copy2(asset, native_target / asset.name)

    runtime_lib_candidates = (
        extract_dir / "runtimes" / rid / "lib" / "net10.0",
        extract_dir / "runtimes" / _rid_family(rid) / "lib" / "net10.0",
    )
    for candidate in runtime_lib_candidates:
        if candidate.exists():
            for asset in candidate.iterdir():
                if asset.is_file() and asset.suffix.lower() in {".dll", ".json"}:
                    shutil.copy2(asset, target_dir / asset.name)


def _rid_family(rid: str) -> str:
    if rid.startswith("win-"):
        return "win"
    if rid.startswith("linux-"):
        return "linux"
    if rid.startswith("osx-"):
        return "osx"
    return rid


def _should_hydrate_dependency_dependencies(package_id: str) -> bool:
    normalized = package_id.lower()
    return normalized.startswith("microsoft.extensions.") or normalized.startswith("microsoft.ml.onnxruntime")


def _trace_nuget(message: str) -> None:
    if os.getenv("STEMCODE_NUGET_VERBOSE"):
        print(f"[stemcode-nuget] {message}", file=sys.stderr, flush=True)


def _select_nuget_runtime_dir(extract_dir: Path, rid: str) -> Path:
    candidates = (
        extract_dir / "tools" / "stemcode" / rid,
        extract_dir / "tools" / "stemcode" / "any",
        extract_dir / "tools" / rid,
        extract_dir / "tools",
        extract_dir / "lib" / "net10.0",
    )

    for candidate in candidates:
        if (candidate / "StemCode.dll").exists():
            return candidate

    matches = sorted(extract_dir.rglob("StemCode.dll"))
    if matches:
        return matches[0].parent

    raise DotNetLoadError("StemCode.dll was not found inside the downloaded NuGet package.")


def _extract_zip_safe(package_path: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    destination_root = destination.resolve()

    with zipfile.ZipFile(package_path) as package:
        for member in package.infolist():
            target = (destination / member.filename).resolve()
            if destination_root != target and destination_root not in target.parents:
                raise DotNetLoadError("NuGet package contains an unsafe archive path.")

        package.extractall(destination)


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
