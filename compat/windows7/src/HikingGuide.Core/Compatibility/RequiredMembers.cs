namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct|AttributeTargets.Field|AttributeTargets.Property,Inherited=false)]
    internal sealed class RequiredMemberAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All,AllowMultiple=true,Inherited=false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; }=featureName;
        public bool IsOptional { get; init; }
        public const string RefStructs=nameof(RefStructs);
        public const string RequiredMembers=nameof(RequiredMembers);
    }
}
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor,Inherited=false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
