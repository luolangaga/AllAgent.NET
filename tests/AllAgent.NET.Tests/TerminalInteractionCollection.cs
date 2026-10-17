using Xunit;

namespace AllAgent.NET.Tests;

// Real terminal processes have startup deadlines and share host resources.
[CollectionDefinition("Terminal interaction", DisableParallelization = true)]
public sealed class TerminalInteractionCollection;
