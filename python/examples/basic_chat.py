from __future__ import annotations

import os
import sys
from pathlib import Path

from stemcode import StemCodeClient


def main() -> int:
    prompt = " ".join(sys.argv[1:]).strip() or "Say hello from StemCode."
    provider = os.getenv("STEMCODE_PROVIDER", "openai").lower()
    api_key = os.getenv("STEMCODE_API_KEY")
    model = os.getenv("STEMCODE_MODEL")
    workspace = os.getenv("STEMCODE_WORKSPACE", str(Path.cwd()))

    builder = StemCodeClient.builder().with_workspace(workspace).use_build_tool()

    if provider == "openai":
        require_key(api_key, provider)
        builder.use_openai(api_key, model)
    elif provider == "anthropic":
        require_key(api_key, provider)
        builder.use_anthropic(api_key, model)
    elif provider == "openrouter":
        require_key(api_key, provider)
        builder.use_openrouter(api_key, model)
    elif provider == "google-ai-studio":
        require_key(api_key, provider)
        builder.use_google_ai_studio(api_key, model)
    elif provider == "ollama":
        builder.use_ollama(os.getenv("STEMCODE_BASE_URL"), model)
    elif provider == "lm-studio":
        builder.use_lm_studio(os.getenv("STEMCODE_BASE_URL"), model)
    elif provider == "openai-compatible":
        require_key(api_key, provider)
        base_url = os.getenv("STEMCODE_BASE_URL")
        if not base_url:
            raise SystemExit("STEMCODE_BASE_URL is required for openai-compatible.")
        builder.use_openai_compatible(base_url, api_key, model)
    else:
        raise SystemExit(f"Unsupported STEMCODE_PROVIDER: {provider}")

    with builder.build() as client:
        client.on_assistant_message_chunk(print, end="", flush=True)
        client.on_status(lambda severity, message: print(f"\n[{severity}] {message}"))

        session = client.initialize()
        print(f"StemCode session {session.session_id} using {session.provider_name}/{session.model_id}")
        result = client.run_turn(prompt)

        if result.response_text:
            print("\n\nFinal response:")
            print(result.response_text)

    return 0


def require_key(api_key: str | None, provider: str) -> None:
    if not api_key:
        raise SystemExit(f"STEMCODE_API_KEY is required for {provider}.")


if __name__ == "__main__":
    raise SystemExit(main())
