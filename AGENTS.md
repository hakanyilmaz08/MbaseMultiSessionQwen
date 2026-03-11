# AGENTS.md

## Scope

This file applies to the entire repository rooted at `/Users/hakan/repos/MbaseMultiSessionQwen`.
If a deeper `AGENTS.md` exists in a subdirectory, that file overrides this one for files under that subtree.

## Repository Shape

- Solution file: `SocialDilemmaLLMSimulation.sln`
- Main project: `SocialDilemmaLLMSimulation/SocialDilemmaLLMSimulation.csproj`
- Entrypoint: `SocialDilemmaLLMSimulation/Program.cs`
- Runtime configuration: `SocialDilemmaLLMSimulation/Properties/launchSettings.json`
- Session and experiment data may exist in checked-in files such as `sessions.json`, `ipd_results.db`, and `SocialDilemmaLLMSimulation/ipd_results.db`

This repository is a .NET 8 research and experimentation project for running multi-session LLM interactions and game-style experiment flows such as IPD and ISD.

## Working Rules

- Read the relevant code paths before changing behavior. For most tasks, start with `Program.cs` and the directly related service or data classes.
- Prefer small, local changes over broad refactors.
- Reuse existing patterns for session management, model settings, brokers, logging, and persistence.
- Use `rg` / `rg --files` for search instead of slower tools when possible.
- Do not introduce new dependencies unless they are necessary for the task.

## Reproducibility Rules

- Treat changes to prompts, model selection, sampling parameters, session state handling, logging, and result export as experiment-affecting changes.
- Do not silently change defaults for environment variables, launch profiles, session storage paths, or database output behavior.
- Prefer configuration-driven behavior over hardcoded model/provider settings.
- If a change alters experiment behavior or output format, call that out explicitly in your final response.
- Preserve compatibility with existing persisted data where practical, especially `sessions.json` and sqlite result files.

## Configuration Guidance

- Treat launch settings as default startup bootstrap configuration, not the sole source of runtime model/provider selection.
- Before adding a new knob, check whether an existing environment-variable pattern already covers it.
- Keep provider-specific behavior isolated from core experiment/session logic.
- Avoid baking machine-specific endpoints or secrets directly into code.

## Data Safety

- Assume repository data files may be meaningful research artifacts, not disposable fixtures.
- Do not delete or overwrite session/result files unless the user explicitly asks for that behavior.
- Be careful with code paths that reset engine state, delete sessions, or rewrite exported outputs.

## Implementation Guidance

- Keep experiment logic separate from infrastructure and transport code.
- Keep provider/model integration code modular so additional backends can be added without rewriting core session flow.
- Prefer explicit code over abstraction layers that hide experiment behavior.
- Add brief comments only where the control flow or experiment logic would otherwise be hard to follow.

## Verification

- For code changes, run the narrowest meaningful verification first.
- Preferred verification starts with `dotnet build SocialDilemmaLLMSimulation.sln`.
- If you change runtime behavior, run a targeted `dotnet run --project SocialDilemmaLLMSimulation/SocialDilemmaLLMSimulation.csproj` flow only when the required local model endpoints/configuration are available.
- If you cannot run a realistic verification path because local model servers or environment settings are unavailable, say so clearly.

## When To Analyze First

Analyze before implementing when the task involves:

- changing persisted data formats
- changing experiment orchestration flow
- adding a new provider or transport path
- modifying result logging/export semantics
- altering launch-profile or configuration conventions used by existing experiments

## Final Response Expectations

- Be concise and specific about what changed.
- Mention any reproducibility-sensitive behavior changes.
- Mention verification performed, and any verification you could not run.


