@echo off
rem Launches the fake ACP agent. VsAgentService always passes --acp first, which node would
rem treat as one of its own options, so the script path goes in front here.
node "%~dp0fake-acp-agent.js" %*
