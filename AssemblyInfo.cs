using System.Diagnostics;

// The HUD compiles source plugins in Debug: Roslyn then marks the assembly DisableOptimizations, and the JIT runs every
// method unoptimized (measured: the bar batch ran several times slower). Roslyn only synthesizes that attribute when the
// assembly doesn't declare one, so declaring it here without DisableOptimizations lets the JIT optimize this plugin.
// Breakpoints still bind; locals may show as optimized away in a debugger.
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
