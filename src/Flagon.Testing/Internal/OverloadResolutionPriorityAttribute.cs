#if !NET9_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill of .NET 9's attribute for the net8.0 build; the C# 13 compiler recognises it by name. Lets the variants
/// overload of <c>TestFlags.SetFlag</c> win over the object-value overload when the default variant is a literal null.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class OverloadResolutionPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}
#endif
