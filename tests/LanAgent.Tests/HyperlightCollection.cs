using Xunit;

namespace LanAgent.Tests;

// Deadline assertions need an uncontended native hypervisor. The delegation test still
// exercises two actual microVMs concurrently within a single acceptance scenario.
[CollectionDefinition("Hyperlight acceptance", DisableParallelization = true)]
public sealed class HyperlightCollection;
