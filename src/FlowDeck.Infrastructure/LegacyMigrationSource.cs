using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Implemented by compiled, already-open service drivers. These aliases identify old settings, not permission to write.
public interface ILegacyMigrationSource
{
    MigratedReference[] LegacyReferences();
}
public sealed record LegacyMigrationPreparation(DeviceConfigurationPackage Configuration, LegacyMigrationBindings Matching,
    MigratedReference[] Unresolved);
