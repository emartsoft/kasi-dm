namespace Kasi.Core;

/// <summary>Base of everything the engine reports. View models subscribe to these; the engine never calls the UI.</summary>
public abstract record EngineEvent;
