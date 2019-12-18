using System;
using System.Reflection;

namespace IL2CXX
{
    public interface IBuiltin
    {
        (string members, bool managed) GetMembers(Transpiler transpiler, Type type);
        string GetInitialize(Transpiler transpiler, Type type);
        string GetBody(Transpiler transpiler, MethodBase method);
    }
}