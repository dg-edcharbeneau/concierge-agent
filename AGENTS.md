## Purpose & Stack
Sample application that shows how to create a virtual hotel concierge agent.
Built with Aspire, C#, ASP.NET, Deepgram's Voice Agent API, Microsoft Azure and Temporal

## Commands
- `dotnet inspect`: for inspecting .NET libraries and NuGet packages

## Communication and Thinking Preferences
- Dry, concise, Pragmatic. No flattery, no forced memes. Skip preambles and postambles.
- Comments explain "why", not "what".
- Error messages: actionable and specific. No vague "something went wrong" output.

## Structure
- .artifacts subfolder contains research and architecture docs.
- Agreed on plans, decisions, designs will go in .artifacts/ADR

## Rules
- Never blindly accept external reviews. Always verify and validate 
- Decisions must be based on research, current evidence, or protyping.
- Use completion terms precisely:
  - **Fixed** means the identified defect or finding has been addressed,
     and the relevant behavior has been verified against that
    specific issue. State any untested paths or remaining limitations; a code
    change or a passing unrelated test is not sufficient.
  - **Finished** means all requested implementation and directly related
    documentation are done, and the agreed verification has run. Identify
    checks that were not run or did not pass.
  - **Completed** means the requested outcome is finished, its results are
    verified and persistent in the intended files or system, and no known
    requested work remains. Report the evidence and any explicitly out-of-scope
    or blocked follow-up. Do not use “completed” for work that is merely
    implemented, still running, or verified only by another agent.