﻿using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace IL2CXX
{
    public interface IBuiltin
    {
        (string members, bool managed) GetMembers(Transpiler transpiler, Type type);
        string GetInitialize(Transpiler transpiler, Type type);
        string GetBody(Transpiler transpiler, MethodBase method);
    }
    public class Transpiler
    {
        private const BindingFlags declaredAndInstance = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public struct MethodKey : IEquatable<MethodKey>
        {
            private readonly RuntimeMethodHandle method;
            private readonly RuntimeTypeHandle type;

            public MethodKey(MethodBase method)
            {
                this.method = method.MethodHandle;
                type = method.DeclaringType.TypeHandle;
            }
            public static bool operator ==(MethodKey x, MethodKey y) => x.method == y.method && x.type.Equals(y.type);
            public static bool operator !=(MethodKey x, MethodKey y) => !(x == y);
            public bool Equals(MethodKey x) => this == x;
            public override bool Equals(object x) => x is MethodKey y && this == y;
            public override int GetHashCode() => method.GetHashCode() ^ type.GetHashCode();
        }
        private static MethodKey ToKey(MethodBase method) => new MethodKey(method);
        public class RuntimeDefinition : IEqualityComparer<Type[]>
        {
            bool IEqualityComparer<Type[]>.Equals(Type[] x, Type[] y) => x.SequenceEqual(y);
            int IEqualityComparer<Type[]>.GetHashCode(Type[] x) => x.Select(y => y.GetHashCode()).Aggregate((y, z) => y % z);

            public readonly Type Type;
            public bool IsManaged = false;
            public readonly List<MethodInfo> Methods = new List<MethodInfo>();
            public readonly Dictionary<MethodKey, int> MethodToIndex = new Dictionary<MethodKey, int>();

            public RuntimeDefinition(Type type) => Type = type;
            protected void Add(MethodInfo method, Dictionary<MethodKey, Dictionary<Type[], int>> genericMethodToTypesToIndex)
            {
                var key = ToKey(method);
                MethodToIndex.Add(key, Methods.Count);
                Methods.Add(method);
                if (method.IsGenericMethod) genericMethodToTypesToIndex.Add(key, new Dictionary<Type[], int>(this));
            }
            protected virtual int GetIndex(MethodKey method) => throw new NotSupportedException();
            public int GetIndex(MethodBase method) => GetIndex(ToKey(method));
        }
        class InterfaceDefinition : RuntimeDefinition
        {
            public InterfaceDefinition(Type type, Dictionary<MethodKey, Dictionary<Type[], int>> genericMethodToTypesToIndex) : base(type)
            {
                IsManaged = true;
                foreach (var x in Type.GetMethods()) Add(x, genericMethodToTypesToIndex);
            }
            protected override int GetIndex(MethodKey method) => MethodToIndex[method];
        }
        class TypeDefinition : RuntimeDefinition
        {
            private static readonly MethodKey finalizeKeyOfObject = new MethodKey(finalizeOfObject);

            public readonly TypeDefinition Base;
            public readonly Dictionary<Type, MethodInfo[]> InterfaceToMethods = new Dictionary<Type, MethodInfo[]>();

            public TypeDefinition(Type type, TypeDefinition @base, Dictionary<MethodKey, Dictionary<Type[], int>> genericMethodToTypesToIndex, IEnumerable<(Type Type, InterfaceDefinition Definition)> interfaces) : base(type)
            {
                Base = @base;
                IsManaged = Type == typeof(object) || Type != typeof(ValueType) && Base.IsManaged;
                if (Base != null) Methods.AddRange(Base.Methods);
                foreach (var x in Type.GetMethods(declaredAndInstance).Where(x => x.IsVirtual))
                {
                    var i = GetIndex(x.GetBaseDefinition());
                    if (i < 0)
                        Add(x, genericMethodToTypesToIndex);
                    else
                        Methods[i] = x;
                }
                foreach (var (key, definition) in interfaces)
                {
                    var methods = new MethodInfo[definition.Methods.Count];
                    var map = (Type.IsArray && key.IsGenericType ? typeof(SZArrayHelper<>).MakeGenericType(GetElementType(Type)) : Type).GetInterfaceMap(key);
                    foreach (var (i, t) in map.InterfaceMethods.Zip(map.TargetMethods, (i, t) => (i, t))) methods[definition.GetIndex(i)] = t;
                    InterfaceToMethods.Add(key, methods);
                }
            }
            protected override int GetIndex(MethodKey method) => MethodToIndex.TryGetValue(method, out var i) ? i : Base?.GetIndex(method) ?? -1;
        }
        struct NativeInt { }
        struct TypedReferenceTag { }
        class Stack : IEnumerable<Stack>
        {
            private readonly Transpiler transpiler;
            public readonly Stack Pop;
            public readonly Dictionary<string, int> Indices;
            public readonly Type Type;
            public readonly string VariableType;
            public readonly string Variable;

            public Stack(Transpiler transpiler)
            {
                this.transpiler = transpiler;
                Indices = new Dictionary<string, int>();
            }
            private Stack(Stack pop, Type type)
            {
                transpiler = pop.transpiler;
                Pop = pop;
                Indices = new Dictionary<string, int>(Pop.Indices);
                Type = type;
                string prefix;
                if (Type.IsByRef || Type.IsPointer)
                {
                    VariableType = "void*";
                    prefix = "p";
                }
                else if (primitives.ContainsKey(Type))
                {
                    if (Type == typeof(long) || Type == typeof(ulong))
                    {
                        VariableType = "int64_t";
                        prefix = "j";
                    }
                    else if (Type == typeof(float) || Type == typeof(double))
                    {
                        VariableType = "double";
                        prefix = "f";
                    }
                    else if (Type == typeof(NativeInt))
                    {
                        VariableType = "intptr_t";
                        prefix = "q";
                    }
                    else
                    {
                        VariableType = "int32_t";
                        prefix = "i";
                    }
                }
                else if (Type.IsEnum)
                {
                    var underlying = Type.GetEnumUnderlyingType();
                    if (underlying == typeof(long) || underlying == typeof(ulong))
                    {
                        VariableType = "int64_t";
                        prefix = "j";
                    }
                    else
                    {
                        VariableType = "int32_t";
                        prefix = "i";
                    }
                }
                else if (Type.IsValueType)
                {
                    VariableType = transpiler.EscapeForScoped(Type);
                    prefix = $"v{transpiler.Escape(Type)}__";
                }
                else
                {
                    VariableType = "t_scoped<t_slot>";
                    prefix = "o";
                }
                Indices.TryGetValue(VariableType, out var index);
                Variable = prefix + index;
                Indices[VariableType] = ++index;
                transpiler.definedIndices.TryGetValue(VariableType, out var defined);
                if (index > defined.Index) transpiler.definedIndices[VariableType] = (prefix, index);
            }
            public Stack Push(Type type) => new Stack(this, type);
            public IEnumerator<Stack> GetEnumerator()
            {
                for (var x = this; x != null; x = x.Pop) yield return x;
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public bool IsPointer => VariableType == "void*";
        }
        class Instruction
        {
            public OpCode OpCode;
            public Func<int, Stack, (int, Stack)> Estimate;
            public Func<int, Stack, int> Generate;
        }

        private static readonly OpCode[] opcodes1 = new OpCode[256];
        private static readonly OpCode[] opcodes2 = new OpCode[256];
        private static readonly Regex unsafeCharacters = new Regex(@"(\W|_)", RegexOptions.Compiled);
        private static string Escape(string name) => unsafeCharacters.Replace(name, m => string.Join(string.Empty, m.Value.Select(x => $"_{(int)x:x}")));
        private static readonly IReadOnlyDictionary<Type, string> builtinTypes = new Dictionary<Type, string> {
            [typeof(object)] = "t_object",
            [typeof(MemberInfo)] = "t__member_info",
            [typeof(Type)] = "t__type"
        };
        private static readonly IReadOnlyDictionary<Type, string> primitives = new Dictionary<Type, string> {
            [typeof(bool)] = "bool",
            [typeof(byte)] = "uint8_t",
            [typeof(sbyte)] = "int8_t",
            [typeof(short)] = "int16_t",
            [typeof(ushort)] = "uint16_t",
            [typeof(int)] = "int32_t",
            [typeof(uint)] = "uint32_t",
            [typeof(long)] = "int64_t",
            [typeof(ulong)] = "uint64_t",
            [typeof(NativeInt)] = "intptr_t",
            [typeof(char)] = "char16_t",
            [typeof(double)] = "double",
            [typeof(float)] = "float",
            [typeof(void)] = "void"
        };
        private static readonly Type typedReferenceByRefType = typeof(TypedReferenceTag).MakeByRefType();
        private static readonly IReadOnlyDictionary<(string, string), Type> typeOfAdd = new Dictionary<(string, string), Type> {
            [("int32_t", "int32_t")] = typeof(int),
            [("int32_t", "intptr_t")] = typeof(NativeInt),
            [("int32_t", "void*")] = typeof(void*),
            [("int64_t", "int64_t")] = typeof(long),
            [("intptr_t", "int32_t")] = typeof(NativeInt),
            [("intptr_t", "intptr_t")] = typeof(NativeInt),
            [("intptr_t", "void*")] = typeof(void*),
            [("double", "double")] = typeof(double),
            [("void*", "int32_t")] = typeof(void*),
            [("void*", "intptr_t")] = typeof(void*),
            [("void*", "void*")] = typeof(NativeInt)
        };
        private static readonly IReadOnlyDictionary<(string, string), Type> typeOfDiv_Un = new Dictionary<(string, string), Type> {
            [("int32_t", "int32_t")] = typeof(int),
            [("int32_t", "intptr_t")] = typeof(NativeInt),
            [("int64_t", "int64_t")] = typeof(long),
            [("intptr_t", "int32_t")] = typeof(NativeInt),
            [("intptr_t", "intptr_t")] = typeof(NativeInt)
        };
        private static readonly IReadOnlyDictionary<(string, string), Type> typeOfShl = new Dictionary<(string, string), Type> {
            [("int32_t", "int32_t")] = typeof(int),
            [("int32_t", "intptr_t")] = typeof(NativeInt),
            [("int64_t", "int32_t")] = typeof(long),
            [("int64_t", "intptr_t")] = typeof(long),
            [("intptr_t", "int32_t")] = typeof(NativeInt),
            [("intptr_t", "intptr_t")] = typeof(NativeInt)
        };
        private static MethodInfo FinalizeOf(Type x) => x.GetMethod(nameof(Finalize), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo finalizeOfObject = FinalizeOf(typeof(object));

        static Transpiler()
        {
            foreach (var x in typeof(OpCodes).GetFields(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public))
            {
                var opcode = (OpCode)x.GetValue(null);
                (opcode.Size == 1 ? opcodes1 : opcodes2)[opcode.Value & 0xff] = opcode;
            }
        }

        private readonly IBuiltin builtin;
        private readonly Action<string> log;
        private readonly Instruction[] instructions1 = new Instruction[256];
        private readonly Instruction[] instructions2 = new Instruction[256];
        private readonly StringWriter typeDeclarations = new StringWriter();
        private readonly StringWriter typeDefinitions = new StringWriter();
        private readonly StringWriter staticDeclarations = new StringWriter();
        private readonly StringWriter threadStaticDeclarations = new StringWriter();
        private readonly StringWriter memberDefinitions = new StringWriter();
        private readonly StringWriter fieldDefinitions = new StringWriter();
        private readonly StringWriter functionDeclarations = new StringWriter();
        private readonly StringWriter functionDefinitions = new StringWriter();
        private readonly List<RuntimeDefinition> runtimeDefinitions = new List<RuntimeDefinition>();
        private readonly Dictionary<Type, RuntimeDefinition> typeToRuntime = new Dictionary<Type, RuntimeDefinition>();
        private readonly HashSet<string> typeIdentifiers = new HashSet<string>();
        private readonly Dictionary<Type, string> typeToIdentifier = new Dictionary<Type, string>();
        private readonly HashSet<(Type, string)> methodIdentifiers = new HashSet<(Type, string)>();
        private readonly Dictionary<MethodKey, string> methodToIdentifier = new Dictionary<MethodKey, string>();
        private readonly Dictionary<MethodKey, Dictionary<Type[], int>> genericMethodToTypesToIndex = new Dictionary<MethodKey, Dictionary<Type[], int>>();
        private readonly Queue<Type> queuedTypes = new Queue<Type>();
        private readonly HashSet<MethodKey> visitedMethods = new HashSet<MethodKey>();
        private readonly Queue<MethodBase> queuedMethods = new Queue<MethodBase>();
        private MethodBase method;
        private byte[] bytes;
        private SortedDictionary<string, (string Prefix, int Index)> definedIndices;
        private Dictionary<int, Stack> indexToStack;
        private StringWriter writer;
        private readonly Stack<ExceptionHandlingClause> tries = new Stack<ExceptionHandlingClause>();
        private readonly Stack<StringWriter> writers = new Stack<StringWriter>();
        private Type constrained;
        private bool @volatile;

        private static Type MakeByRefType(Type type) => type == typeof(TypedReference) ? typedReferenceByRefType : type.MakeByRefType();
        private static Type MakePointerType(Type type) => type == typeof(TypedReference) ? typeof(TypedReferenceTag*) : type.MakePointerType();
        private static Type GetElementType(Type type)
        {
            var t = type.GetElementType();
            return t == typeof(TypedReferenceTag) ? typeof(TypedReference) : t;
        }
        private sbyte ParseI1(ref int index) => (sbyte)bytes[index++];
        private short ParseI2(ref int index)
        {
            var x = BitConverter.ToInt16(bytes, index);
            index += 2;
            return x;
        }
        private int ParseI4(ref int index)
        {
            var x = BitConverter.ToInt32(bytes, index);
            index += 4;
            return x;
        }
        private long ParseI8(ref int index)
        {
            var x = BitConverter.ToInt64(bytes, index);
            index += 8;
            return x;
        }
        private float ParseR4(ref int index)
        {
            var x = BitConverter.ToSingle(bytes, index);
            index += 4;
            return x;
        }
        private double ParseR8(ref int index)
        {
            var x = BitConverter.ToDouble(bytes, index);
            index += 8;
            return x;
        }
        private delegate int ParseBranchTarget(ref int index);
        private int ParseBranchTargetI1(ref int index)
        {
            var offset = ParseI1(ref index);
            return index + offset;
        }
        private int ParseBranchTargetI4(ref int index)
        {
            var offset = ParseI4(ref index);
            return index + offset;
        }
        private Type[] GetGenericArguments()
        {
            try
            {
                return method.GetGenericArguments();
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
        private Type ParseType(ref int index) =>
            method.Module.ResolveType(ParseI4(ref index), method.DeclaringType?.GetGenericArguments(), GetGenericArguments());
        private FieldInfo ParseField(ref int index) =>
            method.Module.ResolveField(ParseI4(ref index), method.DeclaringType?.GetGenericArguments(), GetGenericArguments());
        private MethodBase ParseMethod(ref int index) =>
            method.Module.ResolveMethod(ParseI4(ref index), method.DeclaringType?.GetGenericArguments(), GetGenericArguments());
        private static Type GetThisType(MethodBase method)
        {
            var type = method.DeclaringType;
            return type.IsValueType ? MakePointerType(type) : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SZArrayHelper<>) ? type.GetGenericArguments()[0].MakeArrayType() : type;
        }
        private Type GetArgumentType(int index)
        {
            var parameters = method.GetParameters();
            return method.IsStatic ? parameters[index].ParameterType : index > 0 ? parameters[index - 1].ParameterType : GetThisType(method);
        }
        private static Type GetReturnType(MethodBase method) => method is MethodInfo x ? x.ReturnType : typeof(void);
        private Stack EstimateCall(MethodBase method, Stack stack)
        {
            stack = stack.ElementAt(method.GetParameters().Length + (method.IsStatic ? 0 : 1));
            var @return = GetReturnType(method);
            return @return == typeof(void) ? stack : stack.Push(@return);
        }
        private void Estimate(int index, Stack stack)
        {
            log($"enter {index:x04}");
            while (index < bytes.Length)
            {
                if (indexToStack.TryGetValue(index, out var x))
                {
                    var xs = stack.Select(y => y.VariableType);
                    var ys = x.Select(y => y.VariableType);
                    if (!xs.SequenceEqual(ys)) throw new Exception($"{index:x04}: {string.Join("|", xs)} {string.Join("|", ys)}");
                    break;
                }
                indexToStack.Add(index, stack);
                log($"{index:x04}: ");
                var instruction = instructions1[bytes[index++]];
                if (instruction.OpCode == OpCodes.Prefix1) instruction = instructions2[bytes[index++]];
                log($"{instruction.OpCode}");
                (index, stack) = instruction.Estimate?.Invoke(index, stack) ?? throw new Exception($"{instruction.OpCode}");
                log(string.Join(string.Empty, stack.Reverse().Select(y => $"{y.Type}|")));
            }
            log("exit");
        }
        public void Enqueue(MethodBase method) => queuedMethods.Enqueue(method);
        private bool IsManaged(Type x) => !(x.IsByRef || x.IsPointer || x.IsPrimitive || x.IsEnum || x == typeof(NativeInt));
        public RuntimeDefinition Define(Type type)
        {
            if (typeToRuntime.TryGetValue(type, out var definition)) return definition;
            if (type.IsByRef || type.IsPointer)
            {
                definition = new RuntimeDefinition(type);
                typeToRuntime.Add(type, definition);
            }
            else if (type.IsInterface)
            {
                definition = new InterfaceDefinition(type, genericMethodToTypesToIndex);
                typeToRuntime.Add(type, definition);
                typeDeclarations.WriteLine($@"// {type.AssemblyQualifiedName}
struct {Escape(type)}
{{
}};");
            }
            else
            {
                typeToRuntime.Add(type, null);
                var td = new TypeDefinition(type, type.BaseType == null ? null : (TypeDefinition)Define(type.BaseType), genericMethodToTypesToIndex, type.GetInterfaces().Select(x => (x, (InterfaceDefinition)Define(x))));
                void enqueue(MethodInfo m, MethodInfo concrete)
                {
                    if (m.IsGenericMethod)
                        foreach (var k in genericMethodToTypesToIndex[ToKey(m)].Keys)
                            Enqueue(concrete.MakeGenericMethod(k));
                    else if (methodToIdentifier.ContainsKey(ToKey(m)))
                        Enqueue(concrete);
                }
                foreach (var m in td.Methods.Where(x => !x.IsAbstract)) enqueue(m.GetBaseDefinition(), m);
                foreach (var p in td.InterfaceToMethods)
                {
                    var id = typeToRuntime[p.Key];
                    foreach (var m in id.Methods) enqueue(m, p.Value[id.GetIndex(m)]);
                }
                typeToRuntime[type] = definition = td;
                var identifier = Escape(type);
                var staticFields = new List<FieldInfo>();
                var threadStaticFields = new List<FieldInfo>();
                if (!type.IsEnum)
                    foreach (var x in type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        (Attribute.IsDefined(x, typeof(ThreadStaticAttribute)) ? threadStaticFields : staticFields).Add(x);
                var staticMembers = new StringWriter();
                var staticDefinitions = new StringWriter();
                var initialize = builtin.GetInitialize(this, type);
                if (staticFields.Count > 0 || initialize != null || type.TypeInitializer != null)
                {
                    staticDefinitions.WriteLine($@"
struct t__static_{identifier}
{{");
                    if (type.Name == "<PrivateImplementationDetails>")
                        foreach (var x in staticFields)
                        {
                            var bytes = new byte[Marshal.SizeOf(x.FieldType)];
                            RuntimeHelpers.InitializeArray(bytes, x.FieldHandle);
                            if (x.FieldType.Name.StartsWith("__StaticArrayInitTypeSize="))
                                staticDefinitions.WriteLine($"\t{EscapeForScoped(x.FieldType)} {Escape(x)}{{{Escape(x.FieldType)}::t_value{{{string.Join(", ", bytes.Select(y => $"0x{y:x02}"))}}}}};");
                            else
                                staticDefinitions.WriteLine($"\t{EscapeForScoped(x.FieldType)} {Escape(x)}{{static_cast<{EscapeForScoped(x.FieldType)}>(0x{string.Join(string.Empty, bytes.Reverse().Select(y => $"{y:x02}"))})}};");
                        }
                    else
                        foreach (var x in staticFields) staticDefinitions.WriteLine($"\t{EscapeForScoped(x.FieldType)} {Escape(x)}{{}};");
                    staticDefinitions.WriteLine($@"{'\t'}void f_initialize()
{'\t'}{{");
                    if (initialize != null) staticDefinitions.WriteLine(initialize);
                    if (type.TypeInitializer != null)
                    {
                        staticDefinitions.WriteLine($"\t\t{Escape(type.TypeInitializer)}();");
                        Enqueue(type.TypeInitializer);
                    }
                    staticDefinitions.WriteLine($@"{'\t'}}}
}};");
                    foreach (var x in staticFields) fieldDefinitions.WriteLine($@"void* f__field_{identifier}__{Escape(x.Name)}()
{{
{'\t'}return &t_static::v_instance->v_{identifier}->{Escape(x)};
}}");
                    staticMembers.WriteLine($"\tt__lazy<t__static_{identifier}> v_{identifier};");
                }
                var threadStaticMembers = new StringWriter();
                if (threadStaticFields.Count > 0)
                {
                    threadStaticMembers.WriteLine("\tstruct\n\t{");
                    foreach (var x in threadStaticFields) threadStaticMembers.WriteLine($"\t\t{EscapeForScoped(x.FieldType)} {Escape(x)}{{}};");
                    threadStaticMembers.WriteLine($"\t}} v_{identifier};");
                }
                var declaration = $"// {type.AssemblyQualifiedName}";
                if (builtinTypes.TryGetValue(type, out var builtinName))
                {
                    typeDeclarations.WriteLine($"{declaration}\nusing {identifier} = {builtinName};");
                }
                else
                {
                    declaration += $"\nstruct {identifier}";
                    var @base = type.BaseType == null ? string.Empty : $" : {Escape(type.BaseType)}";
                    string members;
                    if (type == typeof(void))
                    {
                        members = string.Empty;
                    }
                    else if (primitives.TryGetValue(type, out var name) || type.IsEnum)
                    {
                        if (name == null) name = primitives[type.GetEnumUnderlyingType()];
                        members = $@"{'\t'}{name} v__value;
{'\t'}void f__construct({name} a_value)
{'\t'}{{
{'\t'}{'\t'}v__value = a_value;
{'\t'}}}
";
                    }
                    else
                    {
                        var mm = builtin.GetMembers(this, type);
                        members = mm.members;
                        if (members == null)
                        {
                            string scan(Type x, string y) => x.IsValueType ? $"{y}.f__scan(a_scan)" : $"a_scan({y})";
                            if (type.IsArray)
                            {
                                var element = GetElementType(type);
                                var elementIdentifier = EscapeForVariable(element);
                                members = $@"{'\t'}t__bound v__bounds[{type.GetArrayRank()}];
{'\t'}{elementIdentifier}* f__data()
{'\t'}{{
{'\t'}{'\t'}return reinterpret_cast<{elementIdentifier}*>(this + 1);
{'\t'}}}
";
                                if (IsManaged(element)) members += $@"{'\t'}void f__scan(t_scan a_scan)
{'\t'}{{
{'\t'}{'\t'}{Escape(type.BaseType)}::f__scan(a_scan);
{'\t'}{'\t'}auto p = f__data();
{'\t'}{'\t'}for (size_t i = 0; i < v__length; ++i) {scan(element, "p[i]")};
{'\t'}}}
";
                                members += $@"{'\t'}t_scoped<t_slot> f__clone() const
{'\t'}{{
{'\t'}{'\t'}auto p = t_object::f_allocate<{identifier}>(sizeof({identifier}) * v__length);
{'\t'}{'\t'}p->v__length = v__length;
{'\t'}{'\t'}std::copy_n(v__bounds, {type.GetArrayRank()}, p->v__bounds);
{'\t'}{'\t'}auto p0 = reinterpret_cast<const {elementIdentifier}*>(this + 1);
{'\t'}{'\t'}auto p1 = p->f__data();
{'\t'}{'\t'}for (size_t i = 0; i < v__length; ++i) new(p1 + i) {elementIdentifier}(p0[i]);
{'\t'}{'\t'}return p;
{'\t'}}}
";
                            }
                            else if (type.DeclaringType?.Name == "<PrivateImplementationDetails>" && type.Name.StartsWith("__StaticArrayInitTypeSize="))
                            {
                                members = $@"{'\t'}{'\t'}uint8_t v__content[{Marshal.SizeOf(type)}];
{'\t'}{'\t'}void f__destruct()
{'\t'}{'\t'}{{
{'\t'}{'\t'}}}
{'\t'}{'\t'}void f__scan(t_scan a_scan)
{'\t'}{'\t'}{{
{'\t'}{'\t'}}}
";
                            }
                            else
                            {
                                var fields = type.GetFields(declaredAndInstance);
                                string variables(string indent)
                                {
                                    var sb = new StringBuilder();
                                    var i = 0;
                                    void variable(FieldInfo x)
                                    {
                                        sb.AppendLine($"{indent}{EscapeForVariable(x.FieldType)} {Escape(x)};");
                                        try
                                        {
                                            i += Marshal.SizeOf(x.FieldType);
                                        } catch { }
                                    }
                                    void padding(int j)
                                    {
                                        if (j > i) sb.AppendLine($"{indent}char v__padding{i}[{j - i}];");
                                        i = j;
                                    }
                                    var layout = type.StructLayoutAttribute;
                                    if (layout?.Value == LayoutKind.Explicit)
                                        foreach (var x in fields)
                                        {
                                            padding(x.GetCustomAttribute<FieldOffsetAttribute>().Value);
                                            variable(x);
                                        }
                                    else
                                        foreach (var x in fields) variable(x);
                                    if (layout != null) padding(layout.Size);
                                    return sb.ToString();
                                }
                                string scanSlots(string indent) => string.Join(string.Empty, fields.Where(x => IsManaged(x.FieldType)).Select(x => $"{indent}{scan(x.FieldType, Escape(x))};\n"));
                                members = type.IsValueType
                                    ? $@"{variables("\t\t")}
{'\t'}{'\t'}void f__destruct()
{'\t'}{'\t'}{{
{string.Join(string.Empty, fields.Where(x => IsManaged(x.FieldType)).Select(x => $"\t\t\t{Escape(x)}.f__destruct();\n"))}{'\t'}{'\t'}}}
{'\t'}{'\t'}void f__scan(t_scan a_scan)
{'\t'}{'\t'}{{
{scanSlots("\t\t\t")}{'\t'}{'\t'}}}
"
                                    : $@"{variables("\t")}
{'\t'}void f__scan(t_scan a_scan)
{'\t'}{{
{(type.BaseType == null ? string.Empty : $"\t\t{Escape(type.BaseType)}::f__scan(a_scan);\n")}{scanSlots("\t\t")}{'\t'}}}
{'\t'}void f__construct({identifier}* a_p) const
{'\t'}{{
{(type.BaseType == null ? string.Empty : $"\t\t{Escape(type.BaseType)}::f__construct(a_p);\n")}{string.Join(string.Empty, fields.Select(x => $"{'\t'}{'\t'}new(&a_p->{Escape(x)}) decltype({Escape(x)})({Escape(x)});\n"))}{'\t'}}}
{'\t'}t_scoped<t_slot> f__clone() const
{'\t'}{{
{'\t'}{'\t'}auto p = t_object::f_allocate<{identifier}>();
{'\t'}{'\t'}f__construct(p);
{'\t'}{'\t'}return p;
{'\t'}}}
";
                                td.IsManaged |= fields.Select(x => x.FieldType).Any(x => IsManaged(x) && (!x.IsValueType || typeToRuntime[x].IsManaged));
                            }
                        }
                        else
                        {
                            td.IsManaged |= mm.managed;
                        }
                        if (type.IsValueType) members = $@"{'\t'}struct t_value
{'\t'}{{
{members}{'\t'}}} v__value;
{'\t'}void f__construct(t_value&& a_value)
{'\t'}{{
{'\t'}{'\t'}new(&v__value) t_value(std::move(a_value));
{'\t'}}}
{'\t'}void f__scan(t_scan a_scan)
{'\t'}{{
{'\t'}{'\t'}v__value.f__scan(a_scan);
{'\t'}}}
";
                    }
                    typeDeclarations.WriteLine($"{declaration};");
                    typeDefinitions.WriteLine($@"
{declaration}{@base}
{{
{members}}};");
                }
                staticDeclarations.Write(staticMembers);
                memberDefinitions.Write(staticDefinitions);
                threadStaticDeclarations.Write(threadStaticMembers);
            }
            runtimeDefinitions.Add(definition);
            return definition;
        }
        private string EscapeType(Type type)
        {
            if (typeToIdentifier.TryGetValue(type, out var name)) return name;
            var escaped = name = $"t_{Escape(type.ToString())}";
            for (var i = 0; !typeIdentifiers.Add(name); ++i) name = $"{escaped}__{i}";
            typeToIdentifier.Add(type, name);
            return name;
        }
        public string Escape(Type type)
        {
            if (type.IsValueType)
            {
                Define(type);
            }
            else
            {
                var e = GetElementType(type);
                var name = e == null ? null : Escape(e);
                queuedTypes.Enqueue(type);
                if (type.IsByRef) return $"{name}&";
                if (type.IsPointer) return $"{name}*";
            }
            return EscapeType(type);
        }
        public string EscapeForVariable(Type type) =>
            type.IsByRef || type.IsPointer ? $"{EscapeForVariable(GetElementType(type))}*" :
            type.IsInterface ? EscapeForVariable(typeof(object)) :
            primitives.TryGetValue(type, out var x) ? x :
            type.IsEnum ? primitives[type.GetEnumUnderlyingType()] :
            type.IsValueType ? $"{Escape(type)}::t_value" :
            $"t_slot_of<{Escape(type)}>";
        public string EscapeForScoped(Type type) =>
            type.IsByRef || type.IsPointer ? $"{EscapeForVariable(GetElementType(type))}*" :
            type.IsInterface ? EscapeForScoped(typeof(object)) :
            primitives.TryGetValue(type, out var x) ? x :
            type.IsEnum ? primitives[type.GetEnumUnderlyingType()] :
            type.IsValueType ? $"t_scoped<{Escape(type)}::t_value>" :
            $"t_scoped<t_slot_of<{Escape(type)}>>";
        public string Escape(FieldInfo field) => $"v_{Escape(field.Name)}";
        public string Escape(MethodBase method)
        {
            var key = ToKey(method);
            if (methodToIdentifier.TryGetValue(key, out var name)) return name;
            var escaped = name = $"f_{EscapeType(method.DeclaringType)}__{Escape(method.Name)}";
            for (var i = 0; !methodIdentifiers.Add((method.DeclaringType, name)); ++i) name = $"{escaped}__{i}";
            methodToIdentifier.Add(key, name);
            return name;
        }
        private string Escape(MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo field:
                    return Escape(field);
                case MethodBase method:
                    return Escape(method);
                default:
                    throw new Exception();
            }
        }
        public string FormatMove(Type type, string variable) =>
            type == typeof(bool) ? $"{variable} != 0" :
            type.IsByRef || type.IsPointer ? $"reinterpret_cast<{EscapeForVariable(type)}>({variable})" :
            type.IsPrimitive || type.IsEnum ? variable :
            $"std::move({variable})";
        private string GenerateCall(MethodBase method, string function, IEnumerable<string> variables)
        {
            var arguments = new List<Type>();
            if (!method.IsStatic) arguments.Add(GetThisType(method));
            arguments.AddRange(method.GetParameters().Select(x => x.ParameterType));
            return $@"{function}({
    string.Join(",", arguments.Zip(variables.Reverse(), (a, v) => $"\n\t\t{FormatMove(a, v)}"))
}{(arguments.Count > 0 ? "\n\t" : string.Empty)})";
        }
        private void GenerateCall(MethodBase method, string function, Stack stack, Stack after)
        {
            var call = GenerateCall(method, function, stack.Take(method.GetParameters().Length + (method.IsStatic ? 0 : 1)).Select(x => x.Variable));
            writer.WriteLine(GetReturnType(method) == typeof(void) ? $"\t{call};" : $"\t{after.Variable} = {call};");
        }
        private string FunctionPointer(MethodBase method)
        {
            var parameters = method.GetParameters().Select(x => x.ParameterType);
            if (!method.IsStatic) parameters = parameters.Prepend(method.DeclaringType.IsValueType ? typeof(object) : GetThisType(method));
            return $"{EscapeForScoped(GetReturnType(method))}(*)({string.Join(", ", parameters.Select(EscapeForScoped))})";
        }
        public (string Site, string Function) GetVirtualFunction(MethodBase method, string target)
        {
            int indexOf(IEnumerable<IReadOnlyList<MethodInfo>> concretes)
            {
                Escape(method);
                var i = typeToRuntime[method.DeclaringType].GetIndex(method);
                foreach (var ms in concretes) Enqueue(ms[i]);
                return i;
            }
            (int, int) genericIndexOf(IEnumerable<IReadOnlyList<MethodInfo>> concretes)
            {
                var gm = ((MethodInfo)method).GetGenericMethodDefinition();
                var i = typeToRuntime[method.DeclaringType].GetIndex(gm);
                var t2i = genericMethodToTypesToIndex[ToKey(gm)];
                var ga = method.GetGenericArguments();
                if (!t2i.TryGetValue(ga, out var j))
                {
                    j = t2i.Count;
                    t2i.Add(ga, j);
                }
                foreach (var ms in concretes) Enqueue(ms[i].MakeGenericMethod(ga));
                return (i, j);
            }
            Enqueue(method);
            if (method.DeclaringType.IsInterface)
            {
                var concretes = runtimeDefinitions.OfType<TypeDefinition>().Select(y => y.InterfaceToMethods.TryGetValue(method.DeclaringType, out var ms) ? ms : null).Where(y => y != null);
                string resolve;
                if (method.IsGenericMethod)
                {
                    var (i, j) = genericIndexOf(concretes);
                    resolve = $"f__generic_resolve<{Escape(method.DeclaringType)}, {i}, {j}>";
                }
                else
                {
                    resolve = $"f__resolve<{Escape(method.DeclaringType)}, {indexOf(concretes)}>";
                }
                return (
                    $@"{'\t'}{{{{static auto site = reinterpret_cast<void*>({resolve});
{'\t'}{{0}};
{'\t'}}}}}",
                    $"reinterpret_cast<{FunctionPointer(method)}>(reinterpret_cast<void*(*)(void*&, t__type*)>(site)(site, {target}->f_type()))"
                );
            }
            else if (method.IsVirtual)
            {
                string at(int i) => $"reinterpret_cast<void**>({target}->f_type() + 1)[{i}]";
                var concretes = runtimeDefinitions.Where(y => y is TypeDefinition && y.Type.IsSubclassOf(method.DeclaringType)).Select(y => y.Methods);
                string resolved;
                if (method.IsGenericMethod)
                {
                    var (i, j) = genericIndexOf(concretes);
                    resolved = $"reinterpret_cast<void**>({at(i)})[{j}]";
                }
                else
                {
                    resolved = at(indexOf(concretes));
                }
                return ("\t{0};", $"reinterpret_cast<{FunctionPointer(method)}>({resolved})");
            }
            else
            {
                return ("\t{0};", Escape(method));
            }
        }
        public string GenerateVirtualCall(MethodBase method, string target, IEnumerable<string> variables, string prefix)
        {
            var (site, function) = GetVirtualFunction(method, target);
            return string.Format(site, prefix + GenerateCall(method, function, variables.Append(target)));
        }
        private void ProcessNextMethod()
        {
            method = queuedMethods.Dequeue();
            var key = ToKey(method);
            if (!visitedMethods.Add(key) || method.IsAbstract) return;
            var builtin = this.builtin.GetBody(this, method);
            var prototype = new StringWriter();
            prototype.Write($@"
// {method.DeclaringType.AssemblyQualifiedName}
// {method}
// {(method.IsPublic ? "public " : string.Empty)}{(method.IsPrivate ? "private " : string.Empty)}{(method.IsStatic ? "static " : string.Empty)}{(method.IsFinal ? "final " : string.Empty)}{(method.IsVirtual ? "virtual " : string.Empty)}{method.MethodImplementationFlags}");
            string attributes(string prefix, ICustomAttributeProvider cap) => string.Join(string.Empty, cap.GetCustomAttributes(false).Select(x => $"\n{prefix}// [{x}]"));
            var parameters = method.GetParameters().Select(x => (
                Prefix: $"{attributes("\t", x)}\n\t// {x}", Type: x.ParameterType
            ));
            if (!method.IsStatic && !(method.IsConstructor && builtin != null)) parameters = parameters.Prepend((string.Empty, GetThisType(method)));
            string argument(Type t, int i) => $"\n\t{EscapeForScoped(t)} a_{i}";
            var arguments = parameters.Select((x, i) => $"{x.Prefix}{argument(x.Type, i)}").ToList();
            if (method is MethodInfo) prototype.Write(attributes(string.Empty, ((MethodInfo)method).ReturnParameter));
            var returns = method is MethodInfo m ? EscapeForScoped(m.ReturnType) : method.IsStatic || builtin == null ? "void" : EscapeForScoped(method.DeclaringType);
            var identifier = Escape(method);
            prototype.Write($@"
{returns}
{identifier}({string.Join(",", arguments)}
)");
            functionDeclarations.Write(prototype);
            functionDeclarations.WriteLine(';');
            if (method.DeclaringType.IsValueType && !method.IsStatic && !method.IsConstructor) functionDeclarations.WriteLine($@"
{returns}
{identifier}__v({string.Join(",", arguments.Skip(1).Prepend(argument(typeof(object), 0)))}
)
{{
{'\t'}{(returns == "void" ? string.Empty : "return ")}{identifier}({
    string.Join(", ", arguments.Skip(1).Select((x, i) => $"std::move(a_{i + 1})").Prepend($"&static_cast<{Escape(method.DeclaringType)}*>(a_0)->v__value"))
});
}}");
            if (builtin != null)
            {
                writer.WriteLine($"{prototype}\n{{\n{builtin}}}");
                return;
            }
            var dllimport = method.GetCustomAttribute<DllImportAttribute>();
            if (dllimport != null)
            {
                functionDeclarations.WriteLine("// DLL import:");
                functionDeclarations.WriteLine($"//\tValue: {dllimport.Value}");
                functionDeclarations.WriteLine($"//\tEntryPoint: {dllimport.EntryPoint}");
                functionDeclarations.WriteLine($"//\tSetLastError: {dllimport.SetLastError}");
                writer.WriteLine(prototype);
                writer.WriteLine('{');
                foreach (var (x, i) in method.GetParameters().Select((x, i) => (Parameter: x, i)).Where(x => Attribute.IsDefined(x.Parameter, typeof(OutAttribute))))
                {
                    if (x.ParameterType == typeof(StringBuilder)) continue;
                    writer.WriteLine($"\t*a_{i} = {{}};");
                    if (x.ParameterType.IsByRef && typeof(SafeHandle).IsAssignableFrom(GetElementType(x.ParameterType))) writer.WriteLine($"\tvoid* p{i};");
                }
                writer.Write($@"{'\t'}static t_library library(""{dllimport.Value}""s, ""{dllimport.EntryPoint ?? method.Name}"");
{'\t'}");
                if (returns != "void") writer.Write("auto result = ");
                var @return = GetReturnType(method);
                writer.Write($"library.f_as<{(typeof(SafeHandle).IsAssignableFrom(@return) ? "void*" : returns)}(*)(");
                writer.WriteLine(string.Join(",", method.GetParameters().Select(x =>
                {
                    if (x.ParameterType == typeof(string)) return dllimport.CharSet == CharSet.Unicode ? "const char16_t*" : "const char*";
                    if (Attribute.IsDefined(x, typeof(OutAttribute)) && x.ParameterType == typeof(StringBuilder)) return dllimport.CharSet == CharSet.Unicode ? "char16_t*" : "char*";
                    if (x.ParameterType.IsByRef)
                    {
                        var e = GetElementType(x.ParameterType);
                        if (e == typeof(IntPtr) || typeof(SafeHandle).IsAssignableFrom(e)) return "void**";
                    }
                    if (typeof(SafeHandle).IsAssignableFrom(x.ParameterType)) return "void*";
                    if (IsManaged(x.ParameterType))
                    {
                        if (x.ParameterType.IsValueType) return EscapeForScoped(x.ParameterType);
                        if (x.ParameterType.IsArray) return $"{EscapeForVariable(GetElementType(x.ParameterType))}*";
                    }
                    return EscapeForScoped(x.ParameterType);
                }).Select(x => $"\n\t\t{x}")));
                writer.Write("\t)>()(");
                writer.WriteLine(string.Join(",", method.GetParameters().Select((x, i) =>
                {
                    if (x.ParameterType == typeof(string))
                    {
                        var s = $"&a_{i}->v__5ffirstChar";
                        return dllimport.CharSet == CharSet.Unicode ? s : $"f__string({{{s}, static_cast<size_t>(a_{i}->v__5fstringLength)}}).c_str()";
                    }
                    if (Attribute.IsDefined(x, typeof(OutAttribute)) && x.ParameterType == typeof(StringBuilder)) return $"a_{i}->v_m_5fChunkChars->f__data()";
                    if (x.ParameterType.IsByRef)
                    {
                        var e = GetElementType(x.ParameterType);
                        if (e == typeof(IntPtr)) return $"&a_{i}->v__5fvalue";
                        if (typeof(SafeHandle).IsAssignableFrom(e)) return $"&p{i}";
                    }
                    if (typeof(SafeHandle).IsAssignableFrom(x.ParameterType)) return $"a_{i}->v_handle";
                    if (IsManaged(x.ParameterType))
                    {
                        if (x.ParameterType.IsValueType) return $"a_{i}";
                        if (x.ParameterType.IsArray) return $"a_{i} ? a_{i}->f__data() : nullptr";
                    }
                    return $"a_{i}";
                }).Select(x => $"\n\t\t{x}")));
                writer.WriteLine("\t);");
                foreach (var (x, i) in method.GetParameters().Select((x, i) => (Parameter: x, i)).Where(x => Attribute.IsDefined(x.Parameter, typeof(OutAttribute))))
                {
                    if (x.ParameterType.IsByRef && typeof(SafeHandle).IsAssignableFrom(GetElementType(x.ParameterType))) writer.WriteLine($"\t(*a_{i})->v_handle = p{i};");
                }
                if (typeof(SafeHandle).IsAssignableFrom(@return))
                {
                    ConstructorInfo getCI(Type type) => type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(IntPtr), typeof(bool) }, null);
                    var ci = getCI(@return) ?? getCI(typeof(SafeHandle));
                    writer.WriteLine($@"{'\t'}auto p = f__new_zerod<{Escape(@return)}>();
{'\t'}{Escape(ci)}(p, result, true);
{'\t'}return p;");
                    Enqueue(ci);
                }
                else if (@return != typeof(void))
                {
                    writer.WriteLine("\treturn result;");
                }
                writer.WriteLine('}');
                return;
            }
            var body = method.GetMethodBody();
            bytes = body?.GetILAsByteArray();
            if (bytes == null)
            {
                functionDeclarations.WriteLine("// TO BE PROVIDED");
                return;
            }
            writer.Write(prototype);
            writer.WriteLine($@"
{{{string.Join(string.Empty, body.ExceptionHandlingClauses.Select(x => $"\n\t// {x}"))}");
            definedIndices = new SortedDictionary<string, (string, int)>();
            indexToStack = new Dictionary<int, Stack>();
            log($"{method.DeclaringType}::[{method}]");
            foreach (var x in body.ExceptionHandlingClauses)
            {
                log($"{x.Flags}");
                log($"\ttry: {x.TryOffset:x04} to {x.TryOffset + x.TryLength:x04}");
                log($"\thandler: {x.HandlerOffset:x04} to {x.HandlerOffset + x.HandlerLength:x04}");
                switch (x.Flags)
                {
                    case ExceptionHandlingClauseOptions.Clause:
                        log($"\tcatch: {x.CatchType}");
                        break;
                    case ExceptionHandlingClauseOptions.Filter:
                        log($"\tfilter: {x.FilterOffset:x04}");
                        break;
                }
            }
            Estimate(0, new Stack(this));
            foreach (var x in body.ExceptionHandlingClauses)
                switch (x.Flags)
                {
                    case ExceptionHandlingClauseOptions.Clause:
                        Estimate(x.HandlerOffset, new Stack(this).Push(x.CatchType));
                        break;
                    case ExceptionHandlingClauseOptions.Filter:
                        Estimate(x.FilterOffset, new Stack(this).Push(typeof(Exception)));
                        break;
                    default:
                        Estimate(x.HandlerOffset, new Stack(this));
                        break;
                }
            log("\n");
            foreach (var x in body.LocalVariables)
                writer.WriteLine($"\t{EscapeForScoped(x.LocalType)} l{x.LocalIndex}{(body.InitLocals ? "{}" : string.Empty)};");
            foreach (var x in definedIndices)
                for (var i = 0; i < x.Value.Index; ++i)
                    writer.WriteLine($"\t{x.Key} {x.Value.Prefix}{i};");
            writer.WriteLine("\tf_epoch_point();");
            var tryBegins = new Queue<ExceptionHandlingClause>(body.ExceptionHandlingClauses.OrderBy(x => x.TryOffset).ThenByDescending(x => x.HandlerOffset + x.HandlerLength));
            var index = 0;
            while (index < bytes.Length)
            {
                while (tryBegins.Count > 0)
                {
                    var clause = tryBegins.Peek();
                    if (index < clause.TryOffset) break;
                    tryBegins.Dequeue();
                    tries.Push(clause);
                    if (clause.Flags == ExceptionHandlingClauseOptions.Finally)
                    {
                        writer.WriteLine("{auto finally = f__finally([&]\n{");
                        writers.Push(writer);
                        writer = new StringWriter();
                        writer.WriteLine("});");
                    }
                    else
                    {
                        writer.WriteLine("try {");
                    }
                }
                if (tries.Count > 0)
                {
                    var clause = tries.Peek();
                    if (index == (clause.Flags == ExceptionHandlingClauseOptions.Filter ? clause.FilterOffset : clause.HandlerOffset))
                        switch (clause.Flags)
                        {
                            case ExceptionHandlingClauseOptions.Clause:
                                writer.WriteLine($@"// catch {clause.CatchType}
}} catch (t_scoped<t_slot> e) {{
{'\t'}if (!(e && e->f_type()->{(clause.CatchType.IsInterface ? "f__implementation" : "f__is")}(&t__type_of<{Escape(clause.CatchType)}>::v__instance))) throw;");
                                break;
                            case ExceptionHandlingClauseOptions.Filter:
                                writer.WriteLine($@"// filter
}} catch ({EscapeForScoped(typeof(Exception))} e) {{");
                                break;
                            case ExceptionHandlingClauseOptions.Finally:
                                writers.Push(writer);
                                writer = new StringWriter();
                                break;
                            case ExceptionHandlingClauseOptions.Fault:
                                writer.WriteLine(@"// fault
} catch (...) {");
                                break;
                        }
                }
                writer.Write($"L_{index:x04}: // ");
                var stack = indexToStack[index];
                var instruction = instructions1[bytes[index++]];
                if (instruction.OpCode == OpCodes.Prefix1) instruction = instructions2[bytes[index++]];
                writer.Write(instruction.OpCode.Name);
                index = instruction.Generate(index, stack);
                if (tries.Count > 0)
                {
                    var clause = tries.Peek();
                    if (index >= clause.HandlerOffset + clause.HandlerLength)
                    {
                        tries.Pop();
                        if (clause.Flags == ExceptionHandlingClauseOptions.Finally)
                        {
                            var f = writer;
                            var t = writers.Pop();
                            writer = writers.Pop();
                            writer.Write(f);
                            writer.Write(t);
                        }
                        writer.WriteLine('}');
                    }
                }
            }
            writer.WriteLine('}');
        }

        public Transpiler(IBuiltin builtin, Action<string> log)
        {
            this.builtin = builtin;
            this.log = log;
            for (int i = 0; i < 256; ++i)
            {
                instructions1[i] = new Instruction { OpCode = opcodes1[i] };
                instructions2[i] = new Instruction { OpCode = opcodes2[i] };
            }
            instructions1[OpCodes.Nop.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack);
                x.Generate = (index, stack) => {
                    writer.WriteLine();
                    return index;
                };
            });
            new[] {
                OpCodes.Ldarg_0,
                OpCodes.Ldarg_1,
                OpCodes.Ldarg_2,
                OpCodes.Ldarg_3
            }.ForEach((opcode, i) => instructions1[opcode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Push(GetArgumentType(i)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = a_{i};");
                    return index;
                };
            }));
            new[] {
                OpCodes.Ldloc_0,
                OpCodes.Ldloc_1,
                OpCodes.Ldloc_2,
                OpCodes.Ldloc_3
            }.ForEach((opcode, i) => instructions1[opcode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Push(method.GetMethodBody().LocalVariables[i].LocalType));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = l{i};");
                    return index;
                };
            }));
            new[] {
                OpCodes.Stloc_0,
                OpCodes.Stloc_1,
                OpCodes.Stloc_2,
                OpCodes.Stloc_3
            }.ForEach((opcode, i) => instructions1[opcode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\tl{i} = {FormatMove(method.GetMethodBody().LocalVariables[i].LocalType, stack.Variable)};");
                    return index;
                };
            }));
            instructions1[OpCodes.Ldarg_S.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    return (index, stack.Push(GetArgumentType(i)));
                };
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = a_{i};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldarga_S.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    return (index, stack.Push(MakePointerType(GetArgumentType(i))));
                };
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = &a_{i};");
                    return index;
                };
            });
            instructions1[OpCodes.Starg_S.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 1, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\ta_{i} = {FormatMove(GetArgumentType(i), stack.Variable)};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldloc_S.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    return (index, stack.Push(method.GetMethodBody().LocalVariables[i].LocalType));
                };
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = l{i};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldloca_S.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    var type = method.GetMethodBody().LocalVariables[i].LocalType;
                    return (index, stack.Push(MakePointerType(type)));
                };
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = &l{i};");
                    return index;
                };
            });
            instructions1[OpCodes.Stloc_S.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 1, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\tl{i} = {FormatMove(method.GetMethodBody().LocalVariables[i].LocalType, stack.Variable)};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldnull.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Push(typeof(object)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = nullptr;");
                    return index;
                };
            });
            new[] {
                OpCodes.Ldc_I4_M1,
                OpCodes.Ldc_I4_0,
                OpCodes.Ldc_I4_1,
                OpCodes.Ldc_I4_2,
                OpCodes.Ldc_I4_3,
                OpCodes.Ldc_I4_4,
                OpCodes.Ldc_I4_5,
                OpCodes.Ldc_I4_6,
                OpCodes.Ldc_I4_7,
                OpCodes.Ldc_I4_8
            }.ForEach((opcode, i) => instructions1[opcode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Push(typeof(int)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {i - 1};");
                    return index;
                };
            }));
            instructions1[OpCodes.Ldc_I4_S.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 1, stack.Push(typeof(int)));
                x.Generate = (index, stack) =>
                {
                    var i = ParseI1(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = {i};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldc_I4.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Push(typeof(int)));
                x.Generate = (index, stack) =>
                {
                    var i = ParseI4(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = {i};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldc_I8.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 8, stack.Push(typeof(long)));
                x.Generate = (index, stack) =>
                {
                    var i = ParseI8(ref index);
                    writer.WriteLine($" {i}\n\t{indexToStack[index].Variable} = {(i > long.MinValue ? $"{i}" : $"{i + 1} - 1")};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldc_R4.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Push(typeof(float)));
                x.Generate = (index, stack) =>
                {
                    var r = ParseR4(ref index);
                    writer.Write($" {r:G9}\n\t{indexToStack[index].Variable} = ");
                    if (float.IsPositiveInfinity(r))
                        writer.WriteLine("std::numeric_limits<float>::infinity();");
                    else if (float.IsNegativeInfinity(r))
                        writer.WriteLine("-std::numeric_limits<float>::infinity();");
                    else if (float.IsNaN(r))
                        writer.WriteLine("std::numeric_limits<float>::quiet_NaN();");
                    else
                        writer.WriteLine($"{r:G9};");
                    return index;
                };
            });
            instructions1[OpCodes.Ldc_R8.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 8, stack.Push(typeof(double)));
                x.Generate = (index, stack) =>
                {
                    var r = ParseR8(ref index);
                    writer.Write($" {r:G17}\n\t{indexToStack[index].Variable} = ");
                    if (double.IsPositiveInfinity(r))
                        writer.WriteLine("std::numeric_limits<double>::infinity();");
                    else if (double.IsNegativeInfinity(r))
                        writer.WriteLine("-std::numeric_limits<double>::infinity();");
                    else if (double.IsNaN(r))
                        writer.WriteLine("std::numeric_limits<double>::quiet_NaN();");
                    else
                        writer.WriteLine($"{r:G17};");
                    return index;
                };
            });
            instructions1[OpCodes.Dup.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Push(stack.Type));
                x.Generate = (index, stack) =>
                {
                    stack = indexToStack[index];
                    writer.WriteLine($"\n\t{stack.Variable} = {stack.Pop.Variable};");
                    return index;
                };
            });
            instructions1[OpCodes.Pop.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Call.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    return (index, EstimateCall(m, stack));
                };
                x.Generate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    writer.WriteLine($" {m.DeclaringType}::[{m}]");
                    GenerateCall(m, Escape(m), stack, indexToStack[index]);
                    Enqueue(m);
                    return index;
                };
            });
            instructions1[OpCodes.Ret.Value].For(x =>
            {
                x.Estimate = (index, stack) => (int.MaxValue, GetReturnType(method) == typeof(void) ? stack : stack.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.Write("\n\treturn");
                    var @return = GetReturnType(method);
                    if (@return != typeof(void))
                    {
                        writer.Write($" {FormatMove(@return, stack.Variable)}");
                        stack = stack.Pop;
                    }
                    writer.WriteLine(";");
                    if (stack.Pop != null) throw new Exception();
                    return index;
                };
            });
            string unsigned(Stack stack) => stack.IsPointer ? $"reinterpret_cast<uintptr_t>({stack.Variable})" : $"static_cast<u{stack.VariableType}>({stack.Variable})";
            string condition_Un(Stack stack, string integer, string @float)
            {
                if (stack.VariableType == "double") return string.Format(@float, stack.Pop.Variable, stack.Variable);
                if (stack.VariableType == "t_scoped<t_slot>") return $"static_cast<t_object*>({stack.Pop.Variable}) {integer} static_cast<t_object*>({stack.Variable})";
                return $"{unsigned(stack.Pop)} {integer} {unsigned(stack)}";
            }
            string @goto(int index, int target) => target < index ? $@"{{
{'\t'}{'\t'}f_epoch_point();
{'\t'}{'\t'}goto L_{target:x04};
{'\t'}}}" : $"goto L_{target:x04};";
            new[] {
                (OpCode: OpCodes.Br_S, Target: (ParseBranchTarget)ParseBranchTargetI1),
                (OpCode: OpCodes.Br, Target: (ParseBranchTarget)ParseBranchTargetI4)
            }.ForEach(baseSet =>
            {
                instructions1[baseSet.OpCode.Value].For(x =>
                {
                    x.Estimate = (index, stack) =>
                    {
                        Estimate(baseSet.Target(ref index), stack);
                        return (int.MaxValue, stack);
                    };
                    x.Generate = (index, stack) =>
                    {
                        var target = baseSet.Target(ref index);
                        writer.WriteLine($" {target:x04}");
                        if (target < index) writer.WriteLine("\tf_epoch_point();");
                        writer.WriteLine($"\tgoto L_{target:x04};");
                        return index;
                    };
                });
                new[] {
                    (OpCode: OpCodes.Brfalse_S, Operator: "!"),
                    (OpCode: OpCodes.Brtrue_S, Operator: string.Empty)
                }.ForEach(set => instructions1[set.OpCode.Value - OpCodes.Br_S.Value + baseSet.OpCode.Value].For(x =>
                {
                    x.Estimate = (index, stack) =>
                    {
                        Estimate(baseSet.Target(ref index), stack.Pop);
                        return (index, stack.Pop);
                    };
                    x.Generate = (index, stack) =>
                    {
                        var target = baseSet.Target(ref index);
                        writer.WriteLine($" {target:x04}\n\t{{bool b = {set.Operator}{stack.Variable};");
                        if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                        writer.WriteLine($"\tif (b) {@goto(index, target)}}}");
                        return index;
                    };
                }));
                new[] {
                    (OpCode: OpCodes.Beq_S, Operator: "=="),
                    (OpCode: OpCodes.Bge_S, Operator: ">="),
                    (OpCode: OpCodes.Bgt_S, Operator: ">"),
                    (OpCode: OpCodes.Ble_S, Operator: "<="),
                    (OpCode: OpCodes.Blt_S, Operator: "<")
                }.ForEach(set => instructions1[set.OpCode.Value - OpCodes.Br_S.Value + baseSet.OpCode.Value].For(x =>
                {
                    x.Estimate = (index, stack) =>
                    {
                        Estimate(baseSet.Target(ref index), stack.Pop.Pop);
                        return (index, stack.Pop.Pop);
                    };
                    x.Generate = (index, stack) =>
                    {
                        var target = baseSet.Target(ref index);
                        var format = stack.Pop.IsPointer || stack.IsPointer ? "reinterpret_cast<char*>({0})" : "{0}";
                        writer.WriteLine($" {target:x04}\n\t{{bool b = {string.Format(format, stack.Pop.Variable)} {set.Operator} {string.Format(format, stack.Variable)};");
                        if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                        if (IsManaged(stack.Pop.Type)) writer.WriteLine($"\t{stack.Pop.Variable}.f__destruct();");
                        writer.WriteLine($"\tif (b) {@goto(index, target)}}}");
                        return index;
                    };
                }));
                new[] {
                    (OpCode: OpCodes.Bne_Un_S, Integer: "!=", Float: "std::isunordered({0}, {1}) || {0} != {1}"),
                    (OpCode: OpCodes.Bge_Un_S, Integer: ">=", Float: "std::isgreaterequal({0}, {1})"),
                    (OpCode: OpCodes.Bgt_Un_S, Integer: ">", Float: "std::isgreater({0}, {1})"),
                    (OpCode: OpCodes.Ble_Un_S, Integer: "<=", Float: "std::islessequal({0}, {1})"),
                    (OpCode: OpCodes.Blt_Un_S, Integer: "<", Float: "std::isless({0}, {1})")
                }.ForEach(set => instructions1[set.OpCode.Value - OpCodes.Br_S.Value + baseSet.OpCode.Value].For(x =>
                {
                    x.Estimate = (index, stack) =>
                    {
                        Estimate(baseSet.Target(ref index), stack.Pop.Pop);
                        return (index, stack.Pop.Pop);
                    };
                    x.Generate = (index, stack) =>
                    {
                        var target = baseSet.Target(ref index);
                        writer.WriteLine($" {target:x04}\n\t{{bool b = {condition_Un(stack, set.Integer, set.Float)};");
                        if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                        if (IsManaged(stack.Pop.Type)) writer.WriteLine($"\t{stack.Pop.Variable}.f__destruct();");
                        writer.WriteLine($"\tif (b) {@goto(index, target)}}}");
                        return index;
                    };
                }));
            });
            instructions1[OpCodes.Switch.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var n = ParseI4(ref index);
                    var next = index + n * 4;
                    for (; n > 0; --n) Estimate(next + ParseI4(ref index), stack.Pop);
                    return (index, stack.Pop);
                };
                x.Generate = (index, stack) =>
                {
                    var n = ParseI4(ref index);
                    var next = index + n * 4;
                    writer.WriteLine($" {n}\n\tswitch({stack.Variable}) {{");
                    for (var i = 0; i < n; ++i) writer.WriteLine($@"{'\t'}case {i}:
{'\t'}{'\t'}goto L_{next + ParseI4(ref index):x04};");
                    writer.WriteLine("\t}");
                    return index;
                };
            });
            void withVolatile(Action action)
            {
                if (@volatile) writer.WriteLine("\tstd::atomic_thread_fence(std::memory_order_consume);");
                action();
                if (@volatile) writer.WriteLine("\tstd::atomic_thread_fence(std::memory_order_consume);");
                @volatile = false;
            }
            new[] {
                (OpCode: OpCodes.Ldind_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Ldind_U1, Type: typeof(byte)),
                (OpCode: OpCodes.Ldind_I2, Type: typeof(short)),
                (OpCode: OpCodes.Ldind_U2, Type: typeof(ushort)),
                (OpCode: OpCodes.Ldind_I4, Type: typeof(int)),
                (OpCode: OpCodes.Ldind_U4, Type: typeof(uint)),
                (OpCode: OpCodes.Ldind_I8, Type: typeof(long)),
                (OpCode: OpCodes.Ldind_I, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Ldind_R4, Type: typeof(float)),
                (OpCode: OpCodes.Ldind_R8, Type: typeof(double))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(set.Type));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    withVolatile(() => writer.WriteLine($"\t{indexToStack[index].Variable} = *reinterpret_cast<{primitives[set.Type]}*>({stack.Variable});"));
                    return index;
                };
            }));
            instructions1[OpCodes.Ldind_Ref.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(GetElementType(stack.Type)));
                x.Generate = (index, stack) =>
                {
                    var after = indexToStack[index];
                    writer.WriteLine();
                    withVolatile(() => writer.WriteLine($"\t{after.Variable} = *static_cast<{EscapeForVariable(after.Type)}*>({stack.Variable});"));
                    return index;
                };
            });
            instructions1[OpCodes.Stind_Ref.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    withVolatile(() => writer.WriteLine($"\t*reinterpret_cast<{EscapeForVariable(typeof(object))}*>({stack.Pop.Variable}) = std::move({stack.Variable});"));
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Stind_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Stind_I2, Type: typeof(short)),
                (OpCode: OpCodes.Stind_I4, Type: typeof(int)),
                (OpCode: OpCodes.Stind_I8, Type: typeof(long)),
                (OpCode: OpCodes.Stind_R4, Type: typeof(float)),
                (OpCode: OpCodes.Stind_R8, Type: typeof(double))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    withVolatile(() => writer.WriteLine($"\t*reinterpret_cast<{primitives[set.Type]}*>({stack.Pop.Variable}) = {stack.Variable};"));
                    return index;
                };
            }));
            instructions1[OpCodes.Stind_I.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    withVolatile(() => writer.WriteLine($"\t*reinterpret_cast<intptr_t*>({stack.Pop.Variable}) = {(stack.VariableType == "void*" ? "reinterpret_cast" : "static_cast")}<intptr_t>({stack.Variable});"));
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Add, Operator: "+", Type: typeOfAdd),
                (OpCode: OpCodes.Sub, Operator: "-", Type: typeOfAdd),
                (OpCode: OpCodes.Mul, Operator: "*", Type: typeOfAdd),
                (OpCode: OpCodes.Div, Operator: "/", Type: typeOfAdd),
                (OpCode: OpCodes.Rem, Operator: "%", Type: typeOfAdd),
                (OpCode: OpCodes.And, Operator: "&", Type: typeOfDiv_Un),
                (OpCode: OpCodes.Or, Operator: "|", Type: typeOfDiv_Un),
                (OpCode: OpCodes.Xor, Operator: "^", Type: typeOfDiv_Un),
                (OpCode: OpCodes.Shl, Operator: "<<", Type: typeOfShl),
                (OpCode: OpCodes.Shr, Operator: ">>", Type: typeOfShl)
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(set.Type[(stack.Pop.VariableType, stack.VariableType)]));
                x.Generate = (index, stack) =>
                {
                    var after = indexToStack[index];
                    string operand(Stack s) => s.IsPointer ? $"reinterpret_cast<intptr_t>({s.Variable})" : s.Variable;
                    var result = $"{operand(stack.Pop)} {set.Operator} {operand(stack)}";
                    if (after.IsPointer) result = $"reinterpret_cast<void*>({result})";
                    writer.WriteLine($"\n\t{after.Variable} = {result};");
                    return index;
                };
            }));
            new[] {
                (OpCode: OpCodes.Div_Un, Operator: "/", Type: typeOfDiv_Un),
                (OpCode: OpCodes.Rem_Un, Operator: "%", Type: typeOfDiv_Un),
                (OpCode: OpCodes.Shr_Un, Operator: ">>", Type: typeOfShl)
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(set.Type[(stack.Pop.VariableType, stack.VariableType)]));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {unsigned(stack.Pop)} {set.Operator} {unsigned(stack)};");
                    return index;
                };
            }));
            new[] {
                (OpCode: OpCodes.Neg, Operator: "-"),
                (OpCode: OpCodes.Not, Operator: "~")
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{stack.Variable} = {set.Operator}{stack.Variable};");
                    return index;
                };
            }));
            new[] {
                (OpCode: OpCodes.Conv_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Conv_I2, Type: typeof(short)),
                (OpCode: OpCodes.Conv_I4, Type: typeof(int)),
                (OpCode: OpCodes.Conv_I8, Type: typeof(long)),
                (OpCode: OpCodes.Conv_R4, Type: typeof(float)),
                (OpCode: OpCodes.Conv_R8, Type: typeof(double)),
                (OpCode: OpCodes.Conv_U4, Type: typeof(uint)),
                (OpCode: OpCodes.Conv_U8, Type: typeof(ulong)),
                (OpCode: OpCodes.Conv_U2, Type: typeof(ushort)),
                (OpCode: OpCodes.Conv_U1, Type: typeof(byte)),
                (OpCode: OpCodes.Conv_I, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Conv_U, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Conv_R_Un, Type: typeof(double))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(set.Type));
                x.Generate = (index, stack) =>
                {
                    writer.Write($"\n\t{indexToStack[index].Variable} = ");
                    var type = primitives[set.Type];
                    if (stack.IsPointer)
                    {
                        writer.WriteLine($"static_cast<{type}>(reinterpret_cast<uintptr_t>({stack.Variable}));");
                    }
                    else if (stack.Type.IsValueType)
                    {
                        writer.WriteLine($"static_cast<{type}>({stack.Variable});");
                    }
                    else
                    {
                        writer.WriteLine($"reinterpret_cast<{type}>(static_cast<t_object*>({stack.Variable}));");
                        if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    }
                    return index;
                };
            }));
            instructions1[OpCodes.Callvirt.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    Define(m.DeclaringType);
                    return (index, EstimateCall(m, stack));
                };
                x.Generate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    writer.WriteLine($" {m.DeclaringType}::[{m}]");
                    var after = indexToStack[index];
                    string generate(string target) => GenerateVirtualCall(m, target,
                        stack.Take(m.GetParameters().Length).Select(y => y.Variable),
                        GetReturnType(m) == typeof(void) ? string.Empty : $"{after.Variable} = "
                    );
                    if (constrained == null)
                    {
                        writer.WriteLine(generate(stack.ElementAt(m.GetParameters().Length).Variable));
                    }
                    else
                    {
                        if (constrained.IsValueType)
                        {
                            if (m.IsVirtual)
                            {
                                var ct = (TypeDefinition)typeToRuntime[constrained];
                                var cm = (m.DeclaringType.IsInterface ? ct.InterfaceToMethods[m.DeclaringType] : (IReadOnlyList<MethodInfo>)ct.Methods)[typeToRuntime[m.DeclaringType].GetIndex(m)];
                                if (cm.DeclaringType == constrained)
                                {
                                    Enqueue(cm);
                                    GenerateCall(cm, Escape(cm), stack, after);
                                }
                                else
                                {
                                    writer.WriteLine($@"{'\t'}{{auto p = f__new_constructed<{Escape(constrained)}>(std::move(*{FormatMove(MakePointerType(constrained), stack.ElementAt(m.GetParameters().Length).Variable)}));
{generate("p")}
{'\t'}}}");
                                }
                            }
                            else
                            {
                                Enqueue(m);
                                GenerateCall(m, Escape(m), stack, after);
                            }
                        }
                        else
                        {
                            writer.WriteLine(generate($"(*static_cast<{Escape(constrained.IsInterface ? typeof(object) : constrained)}**>({stack.ElementAt(m.GetParameters().Length).Variable}))"));
                        }
                        constrained = null;
                    }
                    return index;
                };
            });
            instructions1[OpCodes.Ldobj.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Push(t));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}");
                    withVolatile(() => writer.WriteLine($"\t{indexToStack[index].Variable} = *static_cast<{EscapeForVariable(t)}*>({stack.Variable});"));
                    return index;
                };
            });
            instructions1[OpCodes.Ldstr.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Push(typeof(string)));
                x.Generate = (index, stack) =>
                {
                    var s = method.Module.ResolveString(ParseI4(ref index));
                    using (var sw = new StringWriter())
                    {
                        using (var provider = CodeDomProvider.CreateProvider("CSharp"))
                            provider.GenerateCodeFromExpression(new CodePrimitiveExpression(s), sw, null);
                        var sl = sw.ToString().Replace($"\" +{Environment.NewLine}    \"", string.Empty);
                        writer.WriteLine($" {sl}\n\t{indexToStack[index].Variable} = f__new_string(u{sl}sv);");
                    }
                    return index;
                };
            });
            instructions1[OpCodes.Newobj.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    return (index, stack.ElementAt(m.GetParameters().Length).Push(m.DeclaringType));
                };
                x.Generate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    var after = indexToStack[index];
                    writer.WriteLine($@" {m.DeclaringType}::[{m}]");
                    var parameters = m.GetParameters();
                    var arguments = parameters.Zip(stack.Take(parameters.Length).Reverse(), (p, s) => $"\n\t\t{FormatMove(p.ParameterType, s.Variable)}");
                    if (builtin.GetBody(this, m) != null)
                    {
                        writer.WriteLine($@"{'\t'}{after.Variable} = {Escape(m)}({string.Join(",", arguments)}
{'\t'});");
                    }
                    else
                    {
                        if (m.DeclaringType.IsValueType)
                        {
                            writer.WriteLine($"\t{after.Variable} = {{}};");
                            arguments = arguments.Prepend($"&{after.Variable}");
                        }
                        else
                        {
                            writer.WriteLine($"\t{{auto p = f__new_zerod<{Escape(m.DeclaringType)}>();");
                            arguments = arguments.Prepend("p");
                        }
                        writer.WriteLine($@"{'\t'}{Escape(m)}(
{'\t'}{'\t'}{string.Join(",", arguments)}
{'\t'});");
                        if (!m.DeclaringType.IsValueType) writer.WriteLine($"\t{after.Variable} = std::move(p);}}");
                    }
                    Enqueue(m);
                    return index;
                };
            });
            instructions1[OpCodes.Castclass.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Push(t));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\tif ({stack.Variable} && !{stack.Variable}->f_type()->{(t.IsInterface ? "f__implementation" : "f__is")}(&t__type_of<{Escape(t)}>::v__instance)) throw std::runtime_error(\"InvalidCastException\");");
                    return index;
                };
            });
            instructions1[OpCodes.Isinst.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    ParseType(ref index);
                    return (index, stack.Pop.Push(typeof(object)));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\tif ({stack.Variable} && !{stack.Variable}->f_type()->{(t.IsInterface ? "f__implementation" : "f__is")}(&t__type_of<{Escape(t)}>::v__instance)) {indexToStack[index].Variable} = {{}};");
                    return index;
                };
            });
            instructions1[OpCodes.Unbox.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Push(MakeByRefType(t)));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    if (!t.IsValueType) throw new Exception(t.ToString());
                    writer.WriteLine($@" {t}
{'\t'}{indexToStack[index].Variable} = static_cast<{Escape(t)}*>({stack.Variable});
{'\t'}{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Throw.Value].For(x =>
            {
                x.Estimate = (index, stack) => (int.MaxValue, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\tthrow t_scoped<t_slot>(std::move({stack.Variable}));");
                    return index;
                };
            });
            instructions1[OpCodes.Ldfld.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    return (index, stack.Pop.Push(f.FieldType));
                };
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.WriteLine($" {f.DeclaringType}::[{f}]");
                    withVolatile(() =>
                    {
                        var after = indexToStack[index];
                        writer.Write($"\t{after.Variable} = ");
                        if (stack.Type != typeof(NativeInt) && stack.Type.IsValueType)
                            writer.Write($"{stack.Variable}.");
                        else
                            writer.Write($"{(stack.VariableType == "intptr_t" ? "reinterpret_cast" : "static_cast")}<{Escape(f.DeclaringType)}{(f.DeclaringType.IsValueType ? "::t_value" : string.Empty)}*>({stack.Variable})->");
                        writer.WriteLine($"{Escape(f)};");
                        if (after.Variable != stack.Variable && IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    });
                    return index;
                };
            });
            instructions1[OpCodes.Ldflda.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    return (index, stack.Pop.Push(MakePointerType(f.FieldType)));
                };
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.Write($" {f.DeclaringType}::[{f}]\n\t{indexToStack[index].Variable} = &");
                    if (stack.Type.IsValueType)
                        writer.Write($"{stack.Variable}.");
                    else
                        writer.Write($"static_cast<{Escape(f.DeclaringType)}{(f.DeclaringType.IsValueType ? "::t_value" : string.Empty)}*>({stack.Variable})->");
                    writer.WriteLine($"{Escape(f)};");
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Stfld.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.WriteLine($" {f.DeclaringType}::[{f}]");
                    withVolatile(() =>
                    {
                        writer.WriteLine($"\t{(stack.Pop.VariableType == "intptr_t" ? "reinterpret_cast" : "static_cast")}<{(f.DeclaringType.IsValueType ? EscapeForVariable(f.DeclaringType) : Escape(f.DeclaringType))}*>({stack.Pop.Variable})->{Escape(f)} = {FormatMove(f.FieldType, stack.Variable)};");
                        if (IsManaged(stack.Pop.Type)) writer.WriteLine($"\t{stack.Pop.Variable}.f__destruct();");
                    });
                    return index;
                };
            });
            string @static(FieldInfo x) => Attribute.IsDefined(x, typeof(ThreadStaticAttribute))
                ? $"t_thread_static::v_instance->v_{Escape(x.DeclaringType)}.{Escape(x)}"
                : $"t_static::v_instance->v_{Escape(x.DeclaringType)}->{Escape(x)}";
            instructions1[OpCodes.Ldsfld.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    return (index, stack.Push(f.FieldType));
                };
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.WriteLine($" {f.DeclaringType}::[{f}]");
                    withVolatile(() => writer.WriteLine($"\t{indexToStack[index].Variable} = {@static(f)};"));
                    return index;
                };
            });
            instructions1[OpCodes.Ldsflda.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    return (index, stack.Push(MakePointerType(f.FieldType)));
                };
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.WriteLine($" {f.DeclaringType}::[{f}]\n\t{indexToStack[index].Variable} = &{@static(f)};");
                    return index;
                };
            });
            instructions1[OpCodes.Stsfld.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    var f = ParseField(ref index);
                    writer.WriteLine($" {f.DeclaringType}::[{f}]");
                    withVolatile(() => writer.WriteLine($"\t{@static(f)} = {FormatMove(f.FieldType, stack.Variable)};"));
                    return index;
                };
            });
            instructions1[OpCodes.Stobj.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}");
                    withVolatile(() => writer.WriteLine($"\t*static_cast<{EscapeForVariable(t)}*>({stack.Pop.Variable}) = std::move({stack.Variable});"));
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Conv_Ovf_I1_Un, Type: typeof(sbyte)),
                (OpCode: OpCodes.Conv_Ovf_I2_Un, Type: typeof(short)),
                (OpCode: OpCodes.Conv_Ovf_I4_Un, Type: typeof(int)),
                (OpCode: OpCodes.Conv_Ovf_I8_Un, Type: typeof(long)),
                (OpCode: OpCodes.Conv_Ovf_U1_Un, Type: typeof(byte)),
                (OpCode: OpCodes.Conv_Ovf_U2_Un, Type: typeof(ushort)),
                (OpCode: OpCodes.Conv_Ovf_U4_Un, Type: typeof(uint)),
                (OpCode: OpCodes.Conv_Ovf_U8_Un, Type: typeof(ulong)),
                (OpCode: OpCodes.Conv_Ovf_I_Un, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Conv_Ovf_U_Un, Type: typeof(NativeInt))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(set.Type));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = static_cast<{stack.VariableType}>({stack.Variable});");
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    return index;
                };
            }));
            instructions1[OpCodes.Box.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop.Push(typeof(object)));
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\t{indexToStack[index].Variable} = {string.Format(t.IsValueType ? $"f__new_constructed<{Escape(t)}>({{0}})" : "{0}", $"std::move({stack.Variable})")};");
                    return index;
                };
            });
            instructions1[OpCodes.Newarr.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Push(t.MakeArrayType()));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\t{indexToStack[index].Variable} = f__new_array<{Escape(t.MakeArrayType())}, {EscapeForVariable(t)}>({stack.Variable});");
                    return index;
                };
            });
            instructions1[OpCodes.Ldlen.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(typeof(NativeInt)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($@"
{'\t'}{indexToStack[index].Variable} = static_cast<{Escape(stack.Type)}*>({stack.Variable})->v__length;
{'\t'}{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Ldelema.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Pop.Push(MakePointerType(t)));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    var array = stack.Pop;
                    writer.WriteLine($@" {t}
{'\t'}{indexToStack[index].Variable} = &static_cast<{Escape(array.Type)}*>({array.Variable})->f__data()[{stack.Variable}];
{'\t'}{array.Variable}.f__destruct();");
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Ldelem_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Ldelem_U1, Type: typeof(byte)),
                (OpCode: OpCodes.Ldelem_I2, Type: typeof(short)),
                (OpCode: OpCodes.Ldelem_U2, Type: typeof(ushort)),
                (OpCode: OpCodes.Ldelem_I4, Type: typeof(int)),
                (OpCode: OpCodes.Ldelem_U4, Type: typeof(uint)),
                (OpCode: OpCodes.Ldelem_I8, Type: typeof(long)),
                (OpCode: OpCodes.Ldelem_I, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Ldelem_R4, Type: typeof(float)),
                (OpCode: OpCodes.Ldelem_R8, Type: typeof(double))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(set.Type));
                x.Generate = (index, stack) =>
                {
                    var array = stack.Pop;
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = static_cast<{Escape(set.Type.MakeArrayType())}*>({array.Variable})->f__data()[{stack.Variable}];");
                    writer.WriteLine($"\t{array.Variable}.f__destruct();");
                    return index;
                };
            }));
            instructions1[OpCodes.Ldelem_Ref.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(typeof(object)));
                x.Generate = (index, stack) =>
                {
                    var array = stack.Pop;
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = static_cast<{Escape(array.Type)}*>({array.Variable})->f__data()[{stack.Variable}];");
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Stelem_I, Type: typeof(IntPtr)),
                (OpCode: OpCodes.Stelem_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Stelem_I2, Type: typeof(short)),
                (OpCode: OpCodes.Stelem_I4, Type: typeof(int)),
                (OpCode: OpCodes.Stelem_I8, Type: typeof(long)),
                (OpCode: OpCodes.Stelem_R4, Type: typeof(float)),
                (OpCode: OpCodes.Stelem_R8, Type: typeof(double))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    var array = stack.Pop.Pop;
                    writer.WriteLine($@"
{'\t'}static_cast<{Escape(set.Type.MakeArrayType())}*>({array.Variable})->f__data()[{stack.Pop.Variable}] = static_cast<{EscapeForVariable(set.Type)}>({stack.Variable});
{'\t'}{array.Variable}.f__destruct();");
                    return index;
                };
            }));
            instructions1[OpCodes.Stelem_Ref.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    var array = stack.Pop.Pop;
                    writer.WriteLine($@"
{'\t'}static_cast<{Escape(array.Type)}*>({array.Variable})->f__data()[{stack.Pop.Variable}] = {FormatMove(GetElementType(array.Type), stack.Variable)};
{'\t'}{stack.Variable}.f__destruct();
{'\t'}{array.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Ldelem.Value].For(x =>
            {
                x.Estimate = (index, stack) => {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Pop.Push(t));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    var after = indexToStack[index];
                    var array = stack.Pop;
                    writer.WriteLine($" {t}\n\t{after.Variable} = static_cast<{Escape(array.Type)}*>({array.Variable})->f__data()[{stack.Variable}];");
                    if (after.Variable != array.Variable) writer.WriteLine($"\t{array.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Stelem.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop.Pop.Pop);
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    var array = stack.Pop.Pop;
                    writer.WriteLine($@" {t}
{'\t'}static_cast<{Escape(array.Type)}*>({array.Variable})->f__data()[{stack.Pop.Variable}] = {FormatMove(t, stack.Variable)};
{'\t'}{array.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions1[OpCodes.Unbox_Any.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    return (index, stack.Pop.Push(t));
                };
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    var after = indexToStack[index];
                    writer.WriteLine($" {t}\n\t{after.Variable} = static_cast<{Escape(t.IsInterface ? typeof(object) : t)}*>({stack.Variable}){(t.IsValueType ? "->v__value" : string.Empty)};");
                    if (after.Variable != stack.Variable) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Conv_Ovf_I1, Type: typeof(sbyte)),
                (OpCode: OpCodes.Conv_Ovf_U1, Type: typeof(byte)),
                (OpCode: OpCodes.Conv_Ovf_I2, Type: typeof(short)),
                (OpCode: OpCodes.Conv_Ovf_U2, Type: typeof(ushort)),
                (OpCode: OpCodes.Conv_Ovf_I4, Type: typeof(int)),
                (OpCode: OpCodes.Conv_Ovf_U4, Type: typeof(uint)),
                (OpCode: OpCodes.Conv_Ovf_I8, Type: typeof(long)),
                (OpCode: OpCodes.Conv_Ovf_U8, Type: typeof(ulong)),
                (OpCode: OpCodes.Conv_Ovf_I, Type: typeof(NativeInt)),
                (OpCode: OpCodes.Conv_Ovf_U, Type: typeof(NativeInt))
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(set.Type));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = static_cast<{stack.VariableType}>({stack.Variable});");
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    return index;
                };
            }));
            instructions1[OpCodes.Ldtoken.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    switch (method.Module.ResolveMember(ParseI4(ref index), method.DeclaringType?.GetGenericArguments(), GetGenericArguments()))
                    {
                        case FieldInfo f:
                            return (index, stack.Push(typeof(RuntimeFieldHandle)));
                        case MethodInfo m:
                            return (index, stack.Push(typeof(RuntimeMethodHandle)));
                        case Type t:
                            return (index, stack.Push(typeof(RuntimeTypeHandle)));
                        default:
                            throw new Exception();
                    }
                };
                x.Generate = (index, stack) =>
                {
                    var member = method.Module.ResolveMember(ParseI4(ref index), method.DeclaringType?.GetGenericArguments(), GetGenericArguments());
                    writer.Write($" {member}\n\t{indexToStack[index].Variable} = ");
                    switch (member)
                    {
                        case FieldInfo f:
                            writer.WriteLine($"{Escape(typeof(RuntimeFieldHandle))}::t_value{{f__field_{Escape(f.DeclaringType)}__{Escape(f.Name)}()}};");
                            break;
                        case MethodInfo m:
                            writer.WriteLine($"{Escape(m)}::v__handle;");
                            break;
                        case Type t:
                            writer.WriteLine($"{Escape(typeof(RuntimeTypeHandle))}::t_value{{&t__type_of<{Escape(t)}>::v__instance}};");
                            break;
                    }
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Add_Ovf, Operator: "+"),
                (OpCode: OpCodes.Add_Ovf_Un, Operator: "+"),
                (OpCode: OpCodes.Mul_Ovf, Operator: "*"),
                (OpCode: OpCodes.Mul_Ovf_Un, Operator: "*"),
                (OpCode: OpCodes.Sub_Ovf, Operator: "-"),
                (OpCode: OpCodes.Sub_Ovf_Un, Operator: "-")
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(typeOfAdd[(stack.Pop.VariableType, stack.VariableType)]));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {stack.Pop.Variable} {set.Operator} {stack.Variable};");
                    return index;
                };
            }));
            instructions1[OpCodes.Endfinally.Value].For(x =>
            {
                x.Estimate = (index, stack) => (int.MaxValue, stack);
                x.Generate = (index, stack) => 
                {
                    if (tries.Peek().Flags == ExceptionHandlingClauseOptions.Finally)
                        writer.WriteLine("\n\treturn;");
                    else
                        writer.WriteLine("\n\tthrow;");
                    return index;
                };
            });
            new[] {
                (OpCode: OpCodes.Leave, Target: (ParseBranchTarget)ParseBranchTargetI4),
                (OpCode: OpCodes.Leave_S, Target: (ParseBranchTarget)ParseBranchTargetI1)
            }.ForEach(set => instructions1[set.OpCode.Value].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    Estimate(set.Target(ref index), stack);
                    return (int.MaxValue, stack);
                };
                x.Generate = (index, stack) =>
                {
                    var target = set.Target(ref index);
                    writer.WriteLine($" {target:x04}\n\tgoto L_{target:x04};");
                    return index;
                };
            }));
            new[] {
                (OpCode: OpCodes.Ceq, Operator: "=="),
                (OpCode: OpCodes.Cgt, Operator: ">"),
                (OpCode: OpCodes.Clt, Operator: "<")
            }.ForEach(set => instructions2[set.OpCode.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(typeof(int)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {stack.Pop.Variable} {set.Operator} {stack.Variable} ? 1 : 0;");
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    if (IsManaged(stack.Pop.Type)) writer.WriteLine($"\t{stack.Pop.Variable}.f__destruct();");
                    return index;
                };
            }));
            new[] {
                (OpCode: OpCodes.Cgt_Un, Integer: ">", Float: "std::isgreater({0}, {1})"),
                (OpCode: OpCodes.Clt_Un, Integer: "<", Float: "std::isless({0}, {1})")
            }.ForEach(set => instructions2[set.OpCode.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Pop.Push(typeof(int)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {condition_Un(stack, set.Integer, set.Float)} ? 1 : 0;");
                    if (IsManaged(stack.Type)) writer.WriteLine($"\t{stack.Variable}.f__destruct();");
                    if (IsManaged(stack.Pop.Type)) writer.WriteLine($"\t{stack.Pop.Variable}.f__destruct();");
                    return index;
                };
            }));
            instructions2[OpCodes.Ldftn.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    return (index, stack.Push(MakePointerType(m.GetType())));
                };
                x.Generate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    writer.WriteLine($" {m.DeclaringType}::[{m}]\n\t{indexToStack[index].Variable} = reinterpret_cast<void*>(&{Escape(m)});");
                    Enqueue(m);
                    return index;
                };
            });
            instructions2[OpCodes.Ldvirtftn.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    return (index, stack.Pop.Push(MakePointerType(m.GetType())));
                };
                x.Generate = (index, stack) =>
                {
                    var m = ParseMethod(ref index);
                    var (site, function) = GetVirtualFunction(m, stack.Variable);
                    writer.WriteLine($@" {m.DeclaringType}::[{m}]
{string.Format(site, $"\t{indexToStack[index].Variable} = reinterpret_cast<void*>({function})")}
{'\t'}{stack.Variable}.f__destruct();");
                    return index;
                };
            });
            instructions2[OpCodes.Stloc.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    var i = ParseI4(ref index);
                    writer.WriteLine($" {i}\n\tl{i} = {FormatMove(method.GetMethodBody().LocalVariables[i].LocalType, stack.Variable)};");
                    return index;
                };
            });
            instructions2[OpCodes.Localloc.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(typeof(byte*)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = alloca({stack.Variable});");
                    return index;
                };
            });
            instructions2[OpCodes.Endfilter.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(typeof(Exception)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($@"
{'\t'}if ({stack.Variable}) throw;
{'\t'}{indexToStack[index].Variable} = std::move(e);");
                    return index;
                };
            });
            instructions2[OpCodes.Volatile.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack);
                x.Generate = (index, stack) =>
                {
                    @volatile = true;
                    writer.WriteLine();
                    return index;
                };
            });
            instructions2[OpCodes.Initobj.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Pop);
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\t*reinterpret_cast<{EscapeForVariable(t)}*>({stack.Variable}) = {{}};");
                    return index;
                };
            });
            instructions2[OpCodes.Constrained.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) =>
                {
                    Define(ParseType(ref index));
                    return (index, stack);
                };
                x.Generate = (index, stack) =>
                {
                    constrained = ParseType(ref index);
                    writer.WriteLine($" {constrained}");
                    return index;
                };
            });
            instructions2[OpCodes.Rethrow.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine("\n\tthrow;");
                    return index;
                };
            });
            instructions2[OpCodes.Sizeof.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index + 4, stack.Push(typeof(uint)));
                x.Generate = (index, stack) =>
                {
                    var t = ParseType(ref index);
                    writer.WriteLine($" {t}\n\t{indexToStack[index].Variable} = sizeof({EscapeForVariable(t)});");
                    return index;
                };
            });
            instructions2[OpCodes.Refanytype.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack.Pop.Push(typeof(RuntimeTypeHandle)));
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine($"\n\t{indexToStack[index].Variable} = {EscapeForVariable(typeof(RuntimeTypeHandle))}{{static_cast<t__type*>({stack.Variable}.v_Type.v__5fvalue)}};");
                    return index;
                };
            });
            instructions2[OpCodes.Readonly.Value & 0xff].For(x =>
            {
                x.Estimate = (index, stack) => (index, stack);
                x.Generate = (index, stack) =>
                {
                    writer.WriteLine();
                    return index;
                };
            });
            writer = functionDefinitions;
        }
        private void WriteRuntimeDefinition(RuntimeDefinition definition, TextWriter writer)
        {
            var type = definition.Type;
            var @base = definition is TypeDefinition && FinalizeOf(type).MethodHandle != finalizeOfObject.MethodHandle ? "t__type_finalizee" : "t__type";
            var identifier = Escape(type);
            writer.WriteLine($@"
template<>
struct t__type_of<{identifier}> : {@base}
{{");
            memberDefinitions.Write($@"
t__type_of<{identifier}>::t__type_of() : {@base}({(type.BaseType == null ? "nullptr" : $"&t__type_of<{Escape(type.BaseType)}>::v__instance")}, {{");
            if (definition is TypeDefinition td)
            {
                void writeMethods(IEnumerable<MethodInfo> methods, Func<int, MethodInfo, string, string> pointer, Func<int, int, MethodInfo, string, string> genericPointer, Func<MethodInfo, MethodInfo> origin, string indent)
                {
                    foreach (var (m, i) in methods.Select((x, i) => (x, i))) writer.WriteLine($@"{indent}// {m}
{indent}void* v_method{i} = {(
    m.IsAbstract ? "nullptr" :
    m.IsGenericMethod ? $"&v_generic__{Escape(m)}" :
    methodToIdentifier.ContainsKey(ToKey(m)) ? $"reinterpret_cast<void*>({pointer(i, m, $"{Escape(m)}{(m.DeclaringType.IsValueType ? "__v" : string.Empty)}")})" :
    "nullptr"
)};");
                    foreach (var (m, i) in methods.Where(x => !x.IsAbstract && x.IsGenericMethod).Select((x, i) => (x, i))) writer.WriteLine($@"{indent}struct
{indent}{{
{
    string.Join(string.Empty, genericMethodToTypesToIndex[ToKey(origin(m))].OrderBy(x => x.Value).Select(p =>
    {
        var x = m.MakeGenericMethod(p.Key);
        return $@"{indent}{'\t'}// {x}
{indent}{'\t'}void* v_method{p.Value} = reinterpret_cast<void*>({genericPointer(i, p.Value, x, $"{Escape(x)}{(x.DeclaringType.IsValueType ? "__v" : string.Empty)}")});
";
    }))
}{indent}}} v_generic__{Escape(m)};");
                }
                writeMethods(td.Methods, (i, m, name) => name, (i, j, m, name) => name, x => x.GetBaseDefinition(), "\t");
                foreach (var p in td.InterfaceToMethods)
                {
                    writer.WriteLine($@"{'\t'}struct
{'\t'}{{");
                    var ii = Escape(p.Key);
                    var ms = typeToRuntime[p.Key].Methods;
                    writeMethods(p.Value,
                        (i, m, name) => $"f__method<{ii}, {i}, {identifier}, {FunctionPointer(m)}, {name}>",
                        (i, j, m, name) => $"f__generic_method<{ii}, {i}, {j}, {identifier}, {FunctionPointer(m)}, {name}>",
                        x => ms[Array.IndexOf(p.Value, x)],
                        "\t\t"
                    );
                    writer.WriteLine($"\t}} v_interface__{ii};");
                }
                memberDefinitions.WriteLine(string.Join(",", td.InterfaceToMethods.Select(p => $"\n\t{{&t__type_of<{Escape(p.Key)}>::v__instance, reinterpret_cast<void**>(&v_interface__{Escape(p.Key)})}}")));
                writer.WriteLine($@"{'\t'}virtual void f_scan(t_object* a_this, t_scan a_scan);
{'\t'}virtual t_scoped<t_slot> f_clone(const t_object* a_this);");
                if (type != typeof(void) && type.IsValueType) writer.WriteLine("\tvirtual void f_copy(const char* a_from, size_t a_n, char* a_to);");
            }
            writer.WriteLine($@"{'\t'}t__type_of();
{'\t'}static t__type_of v__instance;
}};");
            memberDefinitions.WriteLine($@"}}, {(definition.IsManaged ? "true" : "false")}, {(type == typeof(void) ? "0" : $"sizeof({EscapeForVariable(type)})")}{(type.IsArray ? $", &t__type_of<{Escape(GetElementType(type))}>::v__instance, {type.GetArrayRank()}" : string.Empty)})
{{
}}
t__type_of<{identifier}> t__type_of<{identifier}>::v__instance;");
            if (definition is TypeDefinition)
            {
                memberDefinitions.WriteLine($@"void t__type_of<{identifier}>::f_scan(t_object* a_this, t_scan a_scan)
{{
{'\t'}static_cast<{identifier}*>(a_this)->f__scan(a_scan);
}}
t_scoped<t_slot> t__type_of<{identifier}>::f_clone(const t_object* a_this)
{{");
                memberDefinitions.WriteLine(
                    type == typeof(void) ? $@"{'\t'}return t_object::f_allocate<{identifier}>();
}}"
                    : type.IsValueType ? $@"{'\t'}auto p = t_object::f_allocate<{identifier}>();
{'\t'}new(&p->v__value) decltype({identifier}::v__value)(static_cast<const {identifier}*>(a_this)->v__value);
{'\t'}return p;
}}
void t__type_of<{identifier}>::f_copy(const char* a_from, size_t a_n, char* a_to)
{{
{'\t'}f__copy(reinterpret_cast<const decltype({identifier}::v__value)*>(a_from), a_n, reinterpret_cast<decltype({identifier}::v__value)*>(a_to));
}}"
                    : $@"{'\t'}return static_cast<const {identifier}*>(a_this)->f__clone();
}}");
            }
        }
        public void Do(MethodInfo method, TextWriter writer)
        {
            Define(typeof(Type));
            Escape(finalizeOfObject);
            Define(typeof(Thread));
            Enqueue(typeof(ThreadStart).GetMethod("Invoke"));
            Enqueue(typeof(ParameterizedThreadStart).GetMethod("Invoke"));
            Define(typeof(string));
            Enqueue(method);
            do
            {
                ProcessNextMethod();
                while (queuedTypes.Count > 0) Define(queuedTypes.Dequeue());
            }
            while (queuedMethods.Count > 0);
            writer.WriteLine(@"#include <il2cxx/base.h>

namespace il2cxx
{
");
            writer.Write(typeDeclarations);
            writer.Write(typeDefinitions);
            writer.Write(functionDeclarations);
            foreach (var x in runtimeDefinitions) WriteRuntimeDefinition(x, writer);
            writer.WriteLine(@"
}

#include <il2cxx/engine.h>
#include <il2cxx/library.h>

namespace il2cxx
{");
            writer.Write(memberDefinitions);
            writer.WriteLine(@"
struct t_static
{");
            writer.Write(staticDeclarations);
            writer.WriteLine($@"
{'\t'}static t_static* v_instance;
{'\t'}t_static()
{'\t'}{{
{'\t'}{'\t'}v_instance = this;
{'\t'}}}
{'\t'}~t_static()
{'\t'}{{
{'\t'}{'\t'}v_instance = nullptr;
{'\t'}}}
}};

t_static* t_static::v_instance;

struct t_thread_static
{{");
            writer.Write(threadStaticDeclarations);
            writer.WriteLine($@"
{'\t'}static IL2CXX__PORTABLE__THREAD t_thread_static* v_instance;
{'\t'}t_thread_static()
{'\t'}{{
{'\t'}{'\t'}v_instance = this;
{'\t'}}}
{'\t'}~t_thread_static()
{'\t'}{{
{'\t'}{'\t'}v_instance = nullptr;
{'\t'}}}
}};

IL2CXX__PORTABLE__THREAD t_thread_static* t_thread_static::v_instance;
");
            writer.WriteLine(fieldDefinitions);
            writer.Write(functionDefinitions);
            writer.WriteLine($@"
void t_engine::f_finalize(t_object* a_p)
{{
{'\t'}reinterpret_cast<void(*)(t_scoped<t_slot_of<t_object>>)>(reinterpret_cast<void**>(a_p->f_type() + 1)[{typeToRuntime[typeof(object)].GetIndex(finalizeOfObject)}])(t_slot(a_p, t_slot::t_pass()));
}}

}}

#include ""slot.cc""
#include ""object.cc""
#include ""type.cc""
#include ""thread.cc""
#include ""engine.cc""

int main(int argc, char* argv[])
{{
{'\t'}using namespace il2cxx;
{'\t'}std::setlocale(LC_ALL, """");
{'\t'}t_engine::t_options options;
{'\t'}options.v_verbose = true;
{'\t'}t_engine engine(options, argc, argv);
{'\t'}auto s = std::make_unique<t_static>();
{'\t'}auto ts = std::make_unique<t_thread_static>();
{'\t'}auto n = {Escape(method)}();
{'\t'}engine.f_shutdown();
{'\t'}return n;
}}");
        }
    }
}
