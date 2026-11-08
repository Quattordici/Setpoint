namespace Setpoint.Core.Providers;

/// <summary>
/// Marks the model type of a provider block, such as the <c>github:</c> or <c>azuredevops:</c> part of a layer.
/// Each adapter owns its block type; the core only carries it.
/// </summary>
public interface IProviderBlock;
