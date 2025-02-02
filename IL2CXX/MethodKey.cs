using System;
using System.Linq;
using System.Reflection;

namespace IL2CXX
{
    public struct MethodKey : IEquatable<MethodKey>
    {
        public static MethodKey ToKey(MethodBase method) => new(method);

        public readonly MethodBase Method;

        public MethodKey(MethodBase method)
        {
            var t = method.DeclaringType;
            Method = t == null || method.ReflectedType == t ? method : t.GetMethod(
                method.Name,
                method.GetGenericArguments().Length,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                method.GetParameters().Select(x => x.ParameterType).ToArray(),
                null
            );
        }
        // TODO: Work around for equality bug.
        //public static bool operator ==(MethodKey x, MethodKey y) => x.Method == y.Method;
        public static bool operator ==(MethodKey x, MethodKey y) => x.Method.DeclaringType.IsArray && (x.Method.Name == "Get" || x.Method.Name == "Set" || x.Method.Name == "Address")
            ? x.Method.DeclaringType == y.Method.DeclaringType && x.Method.Name == y.Method.Name
            : x.Method == y.Method;
        public static bool operator !=(MethodKey x, MethodKey y) => !(x == y);
        public bool Equals(MethodKey x) => this == x;
        public override bool Equals(object x) => x is MethodKey y && this == y;
        public override int GetHashCode() => Method.GetHashCode();
    }
}
