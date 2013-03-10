/* Copyright (C) 2011-2013, Manuel Meitinger
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Aufbauwerk.Tools.NativeNET
{
    static class Program
    {
        static string requiredRuntime = null;
        static readonly Dictionary<ushort, string> ordinalIds = new Dictionary<ushort, string>();
        static readonly Dictionary<string, MethodInfo> idMethods = new Dictionary<string, MethodInfo>();

        static string GetAssemblerPathForRuntime(string runtime)
        {
            // returns the ilasm path for a given runtime
            DirectoryInfo info = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            return Path.Combine(Path.Combine(info.Parent.FullName, runtime), "ilasm.exe");
        }

        static Version GetFileVersion(string path)
        {
            // returns a comparable file version object
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }

        static void ParseInput(List<string> assemblies, Dictionary<string, string> references)
        {
            // parse every assembly and add all methods without ordinals to the remainingIds list
            List<string> remainingIds = new List<string>();
            foreach (string assembly in assemblies)
                ParseAssembly(remainingIds, Path.GetFullPath(assembly), references);

            // assign each method an ordinal, skipping the ones that have been explicitly set
            ushort nextOrdinal = 1;
            foreach (string id in remainingIds)
            {
                while (ordinalIds.ContainsKey(nextOrdinal))
                    nextOrdinal++;
                ordinalIds.Add(nextOrdinal, id);
                nextOrdinal++;
            }
        }

        static void ParseAssembly(List<string> remainingIds, string path, Dictionary<string, string> references)
        {
            // create a handler for resolving assembly references
            ResolveEventHandler resolver = new ResolveEventHandler(delegate(object sender, ResolveEventArgs args)
            {
                // try to load the assembly through the ordinary channels (GAC etc.)...
                try { return Assembly.ReflectionOnlyLoad(args.Name); }
                catch (FileNotFoundException)
                {
                    // ...otherwise load it from a given reference if it matches its name...
                    string fileName = new AssemblyName(args.Name).Name;
                    if (references.TryGetValue(fileName, out fileName))
                        return Assembly.ReflectionOnlyLoadFrom(fileName);

                    // ...if not, try to find either a dll...
                    fileName = Path.Combine(Path.GetDirectoryName(path), fileName + ".dll");
                    if (File.Exists(fileName))
                        return Assembly.ReflectionOnlyLoadFrom(fileName);

                    // ...or an exe in the same path as our currently handled assembly with the requested name
                    fileName = Path.ChangeExtension(fileName, ".exe");
                    if (File.Exists(fileName))
                        return Assembly.ReflectionOnlyLoadFrom(fileName);
                    throw;
                }
            });

            // set the handler while parsing the assembly
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
            try
            {
                // load the assembly
                Assembly assembly = Assembly.ReflectionOnlyLoadFrom(path);

                // set the requiredRuntime field to the highest assembly runtime
                if (string.IsNullOrEmpty(requiredRuntime) || (GetFileVersion(GetAssemblerPathForRuntime(requiredRuntime)) < GetFileVersion(GetAssemblerPathForRuntime(assembly.ImageRuntimeVersion))))
                    requiredRuntime = assembly.ImageRuntimeVersion;

                // parse each module
                foreach (Module module in assembly.GetModules(false))
                    ParseModule(remainingIds, module);
            }
            finally { AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver; }
        }

        static void ParseModule(List<string> remainingIds, Module module)
        {
            // parse all global methods and types in the module
            foreach (MethodInfo method in module.GetMethods())
                ParseMethod(remainingIds, method);
            foreach (Type type in module.GetTypes())
                ParseType(remainingIds, type);
        }

        static void ParseType(List<string> remainingIds, Type type)
        {
            // parse all methods in non-generic types and their nested types
            if (type.IsGenericType)
                return;
            foreach (MethodInfo method in type.GetMethods())
                ParseMethod(remainingIds, method);
            foreach (Type nestedType in type.GetNestedTypes())
                ParseType(remainingIds, nestedType);
        }

        static void ParseMethod(List<string> remainingIds, MethodInfo method)
        {
            // skip all instance, generic and varargs methods 
            if (!method.IsStatic || method.IsGenericMethod || (method.CallingConvention & CallingConventions.VarArgs) != 0)
                return;

            // find the DllExport attribute
            foreach (CustomAttributeData attribute in CustomAttributeData.GetCustomAttributes(method))
            {
                if (attribute.Constructor.DeclaringType.AssemblyQualifiedName == typeof(DllExportAttribute).AssemblyQualifiedName)
                {
                    // read the name and ordinal specified by the attribute
                    string name = null;
                    ushort ordinal = 0;
                    foreach (CustomAttributeNamedArgument arg in attribute.NamedArguments)
                    {
                        switch (arg.MemberInfo.Name)
                        {
                            case "Name":
                                name = (string)arg.TypedValue.Value;
                                break;
                            case "Ordinal":
                                ordinal = (ushort)arg.TypedValue.Value;
                                break;
                        }
                    }

                    // if no name was set, use the method's full name instead
                    if (name == null)
                        name = method.DeclaringType.FullName + "." + method.Name;

                    // add the method to the ordinal and name list
                    if (ordinal == 0)
                        remainingIds.Add(name);
                    else
                        ordinalIds.Add(ordinal, name);
                    idMethods.Add(name, method);
                }
            }
        }

        static void WriteEscapedString(TextWriter writer, string s)
        {
            // write a string within single quotes and escape special chars
            writer.Write("'");
            writer.Write(s.Replace(@"\", @"\\").Replace(@"'", @"\'"));
            writer.Write("'");
        }

        static void WriteType(TextWriter writer, Dictionary<Assembly, string> assemblyAliases, Type type)
        {
            // write the given type in IL
            if (type.IsByRef)
            {
                WriteType(writer, assemblyAliases, type.GetElementType());
                writer.Write("&");
            }
            else if (type.IsArray)
            {
                // NOTE: CLR allows for bounds being set on an array type, C# and VB.NET don't.
                //       I haven't found a way to reflect on these bounds, so they are ignored.
                WriteType(writer, assemblyAliases, type.GetElementType());
                writer.Write("[");
                int rank = type.GetArrayRank();
                if (rank > 1)
                    writer.Write(new string(',', rank - 1));
                writer.Write("]");
            }
            else if (type.IsPointer)
            {
                WriteType(writer, assemblyAliases, type.GetElementType());
                writer.Write("*");
            }
            else if (type == typeof(void))
                writer.Write("void");
            else if (type == typeof(System.Boolean))
                writer.Write("bool");
            else if (type == typeof(System.Char))
                writer.Write("char");
            else if (type == typeof(System.Object))
                writer.Write("object");
            else if (type == typeof(System.String))
                writer.Write("string");
            else if (type == typeof(System.Single))
                writer.Write("float32");
            else if (type == typeof(System.Double))
                writer.Write("float64");
            else if (type == typeof(System.SByte))
                writer.Write("int8");
            else if (type == typeof(System.Int16))
                writer.Write("int16");
            else if (type == typeof(System.Int32))
                writer.Write("int32");
            else if (type == typeof(System.Int64))
                writer.Write("int64");
            else if (type == typeof(System.IntPtr))
                writer.Write("native int");
            else if (type == typeof(System.UIntPtr))
                writer.Write("native unsigned int");
            else if (type == typeof(System.TypedReference))
                writer.Write("typedref");
            else if (type == typeof(System.Byte))
                writer.Write("unsigned int8");
            else if (type == typeof(System.UInt16))
                writer.Write("unsigned int16");
            else if (type == typeof(System.UInt32))
                writer.Write("unsigned int32");
            else if (type == typeof(System.UInt64))
                writer.Write("unsigned int64");
            else
            {
                // if it's not a special type, begin with the its full definition
                writer.Write(type.IsValueType ? "valuetype" : "class");

                //NOTE: mscorlib will always be the current process's version, or in other words:
                //      ReflectionOnlyLoad ignores anything after mscorlib and returns the current one.
                //      Ilasm will resolve mscorlib automatically therefore it's written as-is to
                //      ensure it's the same runtime version as ilasm, i.e. the requiredRuntime.
                writer.Write(" [");
                if (type.Assembly != typeof(RuntimeEnvironment).Assembly)
                {
                    // if it's not mscorlib, generate or get an assembly alias
                    string alias;
                    if (!assemblyAliases.TryGetValue(type.Assembly, out alias))
                        assemblyAliases.Add(type.Assembly, alias = "_" + assemblyAliases.Count);
                    writer.Write(alias);
                }
                else
                    writer.Write("mscorlib");
                writer.Write("]");

                // write the namespace
                if (!string.IsNullOrEmpty(type.Namespace))
                {
                    WriteEscapedString(writer, type.Namespace);
                    writer.Write(".");
                }

                // if it's a nested type, list all parent classes separated with a slash
                Stack<Type> baseTypes = new Stack<Type>();
                Type baseType = type;
                while (baseType.IsNested)
                    baseTypes.Push(baseType = baseType.DeclaringType);
                while (baseTypes.Count > 0)
                {
                    WriteEscapedString(writer, baseTypes.Pop().Name);
                    writer.Write("/");
                }

                // write the name of the type, followed by any generic argument types
                writer.Write(type.Name);
                if (type.IsGenericType)
                {
                    writer.Write("<");
                    Type[] args = type.GetGenericArguments();
                    WriteType(writer, assemblyAliases, args[0]);
                    for (int i = 1; i < args.Length; i++)
                    {
                        writer.Write(",");
                        WriteType(writer, assemblyAliases, args[i]);
                    }
                    writer.Write(">");
                }
            }
        }

        static void WriteVarEnum(TextWriter writer, VarEnum varEnum)
        {
            // write the IL equivalent of a VarEnum value
            switch (varEnum)
            {
                case VarEnum.VT_ARRAY:
                    writer.Write("[]");
                    break;
                case VarEnum.VT_BYREF:
                    writer.Write("&");
                    break;
                case VarEnum.VT_PTR:
                    writer.Write(" *");
                    break;
                case VarEnum.VT_BLOB:
                case VarEnum.VT_BLOB_OBJECT:
                case VarEnum.VT_BOOL:
                case VarEnum.VT_BSTR:
                case VarEnum.VT_CARRAY:
                case VarEnum.VT_CF:
                case VarEnum.VT_CLSID:
                case VarEnum.VT_DATE:
                case VarEnum.VT_DECIMAL:
                case VarEnum.VT_ERROR:
                case VarEnum.VT_FILETIME:
                case VarEnum.VT_HRESULT:
                case VarEnum.VT_INT:
                case VarEnum.VT_LPSTR:
                case VarEnum.VT_LPWSTR:
                case VarEnum.VT_NULL:
                case VarEnum.VT_RECORD:
                case VarEnum.VT_SAFEARRAY:
                case VarEnum.VT_STORAGE:
                case VarEnum.VT_STORED_OBJECT:
                case VarEnum.VT_STREAM:
                case VarEnum.VT_STREAMED_OBJECT:
                case VarEnum.VT_USERDEFINED:
                case VarEnum.VT_VARIANT:
                case VarEnum.VT_VECTOR:
                case VarEnum.VT_VOID:
                    writer.Write(" ");
                    writer.Write(varEnum.ToString().Substring(3).ToLowerInvariant());
                    break;
                case VarEnum.VT_DISPATCH:
                case VarEnum.VT_UNKNOWN:
                    writer.Write(" i");
                    writer.Write(varEnum.ToString().Substring(3).ToLowerInvariant());
                    break;
                case VarEnum.VT_CY:
                    writer.Write(" currency");
                    break;
                case VarEnum.VT_I1:
                case VarEnum.VT_I2:
                case VarEnum.VT_I4:
                case VarEnum.VT_I8:
                    writer.Write(" int");
                    writer.Write((varEnum.ToString()[4] - '0') * 8);
                    break;
                case VarEnum.VT_UI1:
                case VarEnum.VT_UI2:
                case VarEnum.VT_UI4:
                case VarEnum.VT_UI8:
                    writer.Write(" unsigned int");
                    writer.Write((varEnum.ToString()[5] - '0') * 8);
                    break;
                case VarEnum.VT_R4:
                case VarEnum.VT_R8:
                    writer.Write(" float");
                    writer.Write((varEnum.ToString()[4] - '0') * 8);
                    break;
                case VarEnum.VT_UINT:
                    writer.Write(" unsigned int");
                    break;
                case VarEnum.VT_EMPTY:
                    break;
                default:
                    throw new ArgumentOutOfRangeException("VarEnum");
            }
        }

        static void WriteUnmanagedType(TextWriter writer, UnmanagedType unmanagedType, List<CustomAttributeNamedArgument> args)
        {
            // write an unmanaged type in IL and remove all associated properties from the args list
            switch (unmanagedType)
            {
                case UnmanagedType.LPArray:
                    int? sizeConst = null;
                    short? sizeParam = null;
                    for (int i = args.Count - 1; i >= 0; i--)
                    {
                        CustomAttributeNamedArgument arg = args[i];
                        switch (arg.MemberInfo.Name)
                        {
                            case "ArraySubType":
                                UnmanagedType innerType = (UnmanagedType)arg.TypedValue.Value;
                                if (innerType == UnmanagedType.LPArray || innerType == UnmanagedType.SafeArray)
                                    throw new ArgumentOutOfRangeException("ArraySubType");
                                WriteUnmanagedType(writer, innerType, args);
                                break;
                            case "SizeConst":
                                sizeConst = (int)arg.TypedValue.Value;
                                break;
                            case "SizeParamIndex":
                                sizeParam = (short)arg.TypedValue.Value;
                                break;
                            default:
                                continue;
                        }
                        args.RemoveAt(i);
                    }
                    writer.Write("[");
                    if (sizeConst.HasValue)
                        writer.Write(sizeConst.Value);
                    if (sizeParam.HasValue)
                    {
                        writer.Write("+");
                        writer.Write(sizeParam.Value);
                    }
                    writer.Write("]");
                    break;
                case UnmanagedType.SafeArray:
                    writer.Write("safearray");
                    for (int i = args.Count - 1; i >= 0; i--)
                    {
                        CustomAttributeNamedArgument arg = args[i];
                        switch (arg.MemberInfo.Name)
                        {
                            case "SafeArraySubType":
                                WriteVarEnum(writer, (VarEnum)arg.TypedValue.Value);
                                break;
                            default:
                                continue;
                        }
                        args.RemoveAt(i);
                    }
                    break;
                case UnmanagedType.AnsiBStr:
                case UnmanagedType.Bool:
                case UnmanagedType.BStr:
                case UnmanagedType.Currency:
                case UnmanagedType.Error:
                case UnmanagedType.LPStr:
                case UnmanagedType.LPStruct:
                case UnmanagedType.LPTStr:
                case UnmanagedType.LPWStr:
                case UnmanagedType.Struct:
                case UnmanagedType.TBStr:
                    writer.Write(unmanagedType.ToString().ToLowerInvariant());
                    break;
                case UnmanagedType.IDispatch:
                case UnmanagedType.Interface:
                case UnmanagedType.IUnknown:
                    writer.Write(unmanagedType.ToString().ToLowerInvariant());
                    for (int i = args.Count - 1; i >= 0; i--)
                    {
                        CustomAttributeNamedArgument arg = args[i];
                        switch (arg.MemberInfo.Name)
                        {
                            case "IidParameterIndex":
                                writer.Write("(iidparam=");
                                writer.Write(arg.TypedValue.Value);
                                writer.Write(")");
                                break;
                            default:
                                continue;
                        }
                        args.RemoveAt(i);
                    }
                    break;
                case UnmanagedType.FunctionPtr:
                    writer.Write("method");
                    break;
                case UnmanagedType.SysInt:
                    writer.Write("int");
                    break;
                case UnmanagedType.SysUInt:
                    writer.Write("unsigned int");
                    break;
                case UnmanagedType.VariantBool:
                    writer.Write("variant bool");
                    break;
                case UnmanagedType.VBByRefStr:
                    writer.Write("byvalstr");
                    break;
                case UnmanagedType.I1:
                case UnmanagedType.I2:
                case UnmanagedType.I4:
                case UnmanagedType.I8:
                    writer.Write("int");
                    writer.Write((unmanagedType.ToString()[1] - '0') * 8);
                    break;
                case UnmanagedType.U1:
                case UnmanagedType.U2:
                case UnmanagedType.U4:
                case UnmanagedType.U8:
                    writer.Write("unsigned int");
                    writer.Write((unmanagedType.ToString()[1] - '0') * 8);
                    break;
                case UnmanagedType.R4:
                case UnmanagedType.R8:
                    writer.Write("float");
                    writer.Write((unmanagedType.ToString()[1] - '0') * 8);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("UnmanagedType");
            }
        }

        static void WriteMarshal(TextWriter writer, CustomAttributeData attribute)
        {
            // begin the IL for a MarshalAs attribute
            writer.Write(" marshal(");

            // write the unmanaged type and all its associated properties
            List<CustomAttributeNamedArgument> args = new List<CustomAttributeNamedArgument>(attribute.NamedArguments);
            WriteUnmanagedType(writer, (UnmanagedType)attribute.ConstructorArguments[0].Value, args);

            // raise an exception if there are still properties left (e.g. if IidParameterIndex was specified for a non-interface type)
            foreach (CustomAttributeNamedArgument arg in args)
                throw new ArgumentOutOfRangeException(arg.MemberInfo.Name);

            // end the attribute
            writer.Write(")");
        }

        static void WriteParamater(TextWriter writer, Dictionary<Assembly, string> assemblyAliases, ParameterInfo param)
        {
            // write the parameter direction
            if (param.IsIn)
                writer.Write("[in]");
            if (param.IsOut)
                writer.Write("[out]");
            if (param.IsOptional)
                writer.Write("[opt]");

            // write the parameter type
            WriteType(writer, assemblyAliases, param.ParameterType);

            // find and write the DllMarshalAs attribute, if any
            foreach (CustomAttributeData attribute in CustomAttributeData.GetCustomAttributes(param))
                if (attribute.Constructor.DeclaringType.AssemblyQualifiedName == typeof(DllMarshalAsAttribute).AssemblyQualifiedName)
                    WriteMarshal(writer, attribute);
        }

        static void WriteExternAssembly(TextWriter writer, Assembly assembly, string alias)
        {
            // start the reference to an external assembly
            AssemblyName assemblyName = assembly.GetName();
            writer.Write(".assembly extern ");
            WriteEscapedString(writer, assemblyName.Name);
            writer.Write(" as ");

            // use the alias assigned earlier
            writer.Write(alias);
            writer.Write(" { .ver ");
            writer.Write(assemblyName.Version.ToString().Replace('.', ':'));

            // if the assembly was strong signed include the public key
            if (assemblyName.HashAlgorithm == AssemblyHashAlgorithm.SHA1)
            {
                writer.Write(" .publickeytoken = (");
                foreach (byte b in assemblyName.GetPublicKeyToken())
                    writer.Write(b.ToString("X2"));
                writer.Write(")");
            }

            // if the assembly is not culture neutral, also include the culture
            string culture = assemblyName.CultureInfo.Name;
            if (!string.IsNullOrEmpty(culture))
            {
                writer.Write(" .culture ");
                WriteEscapedString(writer, culture);
            }
            writer.Write(" }");
        }

        static void WriteMethod(TextWriter writer, Dictionary<Assembly, string> assemblyAliases, MethodInfo method, ushort ordinal, string name)
        {
            // start the signature of an exported method
            writer.Write(".method public static ");
            WriteParamater(writer, assemblyAliases, method.ReturnParameter);

            // the name will be the method's ordinal
            writer.Write(" _");
            writer.Write(ordinal);

            // serialize all parameters, which include direction, type and marshalling attribute
            writer.Write("(");
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length > 0)
            {
                WriteParamater(writer, assemblyAliases, parameters[0]);
                for (int i = 1; i < parameters.Length; i++)
                {
                    writer.Write(", ");
                    WriteParamater(writer, assemblyAliases, parameters[i]);
                }
            }

            // mark the method for native export and specify its name and ordinal
            writer.Write(") { .export [");
            writer.Write(ordinal);
            writer.Write("] as ");
            WriteEscapedString(writer, name);

            // load every argument onto the stack
            for (int i = 0; i < parameters.Length; i++)
            {
                writer.Write(" ldarg.s ");
                writer.Write(i);
            }

            // now call the managed method, which requires the whole method signature again - without direction and marshalling
            writer.Write(" call ");
            WriteType(writer, assemblyAliases, method.ReturnParameter.ParameterType);
            writer.Write(" ");
            WriteType(writer, assemblyAliases, method.DeclaringType);
            writer.Write("::");
            WriteEscapedString(writer, method.Name);
            writer.Write("(");
            if (parameters.Length > 0)
            {
                WriteType(writer, assemblyAliases, parameters[0].ParameterType);
                for (int i = 1; i < parameters.Length; i++)
                {
                    writer.Write(", ");
                    WriteType(writer, assemblyAliases, parameters[i].ParameterType);
                }
            }

            // return to the caller (i.e. the native stack)
            writer.Write(") ret }");
        }

        static void WriteAssembly(TextWriter writer, out Dictionary<Assembly, string> assemblyAliases, string name, string moduleName)
        {
            // write the generated assembly's signature
            assemblyAliases = new Dictionary<Assembly, string>();
            writer.Write(".assembly ");
            WriteEscapedString(writer, name);

            // TODO: We always specify the version as 1.0 which seems odd. However, since
            //       we allow multiple input assemblies we cannot infer a single version
            //       number. One other possibility might be to set the version to this
            //       program's version.
            writer.Write(" { .ver 1:0:0:0 .hash algorithm 0x00008004 }");
            writer.WriteLine();

            // write the module name (which is the dll file name)
            writer.Write(".module ");
            WriteEscapedString(writer, moduleName);
            writer.WriteLine();

            // finally, write every exported method
            foreach (KeyValuePair<ushort, string> ordinalId in ordinalIds)
            {
                WriteMethod(writer, assemblyAliases, idMethods[ordinalId.Value], ordinalId.Key, ordinalId.Value);
                writer.WriteLine();
            }
        }

        static void WriteOutput(StringBuilder cmdLine, string dllName)
        {
            // create a new temporary output file
            string fileName = Path.GetTempFileName();
            try
            {
                // initialize a file stream and a string writer
                using (StreamWriter writer = new StreamWriter(fileName, false, Encoding.UTF8))
                using (StringWriter memWriter = new StringWriter())
                {
                    // write the assembly to a string first, because this method will generate all assembly aliases
                    Dictionary<Assembly, string> assemblyAliases;
                    WriteAssembly(memWriter, out assemblyAliases, Path.GetFileNameWithoutExtension(dllName), Path.GetFileName(dllName));

                    // now write the referenced assemblies, followed by the previously generated string
                    foreach (KeyValuePair<Assembly, string> assemblyAlias in assemblyAliases)
                    {
                        WriteExternAssembly(writer, assemblyAlias.Key, assemblyAlias.Value);
                        writer.WriteLine();
                    }
                    writer.Write(memWriter);
                }

                // append the file name to ilasm's parameters
                cmdLine.Append(" \"");
                cmdLine.Append(fileName);
                cmdLine.Append("\"");

                // create the start information (redirect ilasm output to our console)
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = GetAssemblerPathForRuntime(requiredRuntime);
                startInfo.Arguments = cmdLine.ToString();
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = false;

                // start ilasm and return its exit/error code
                using (Process process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    Environment.ExitCode = process.ExitCode;
                }
            }
            finally { File.Delete(fileName); }
        }

        static void Main(string[] args)
        {
            try
            {
                // start parsing the command line
                StringBuilder cmdLine = new StringBuilder("/DLL");
                string dllName = null;
                List<string> assemblies = new List<string>();
                Dictionary<string, string> references = new Dictionary<string, string>();
                foreach (string arg in args)
                {
                    // skip empty arguments
                    if (arg.Length == 0)
                        continue;

                    // check if the next argument is an input assembly or an (ilasm) parameter
                    if (arg[0] == '/')
                    {
                        // handle flags and parameters differently
                        int separator = arg.IndexOfAny(new char[] { ':', '=' }, 1);
                        if (separator >= 4)
                        {
                            // handle each recognized paremeter
                            string value = arg.Substring(separator + 1).Trim();
                            switch (arg.Substring(1, 3).ToUpperInvariant())
                            {
                                case "OUT":
                                    // store the output assembly's file name and forward the parameter to ilasm as well
                                    dllName = value;
                                    break;
                                case "REF":
                                    // store the assembly reference and don't forward the parameter
                                    references.Add(Path.GetFileNameWithoutExtension(value), value);
                                    continue;
                            }
                        }
                        else if (separator == -1 && arg.Length >= 4)
                        {
                            // handle each recognized flag
                            switch (arg.Substring(1, 3).ToUpperInvariant())
                            {
                                case "X86":
                                    // in addition to ilasm's /x64 we also support /x86
                                    // but since this is ilasm's default value we simply ingore it
                                    continue;
                            }
                        }

                        // forward the parameters to ilasm
                        cmdLine.Append(" \"");
                        cmdLine.Append(arg);
                        cmdLine.Append("\"");
                    }
                    else
                        // add the input assembly to the list and do not forward it to ilasm (which would mistake it for an .il file)
                        assemblies.Add(arg);
                }

                // ensure that we're at least given one input assembly and an output file name
                if (string.IsNullOrEmpty(dllName) || assemblies.Count == 0)
                    throw new ArgumentException(string.Format("USAGE: {0} [<ilasm options>] /OUT[PUT]=<dll> [/REF[ERENCE]=<assembly> [...]] <assembly> [...]", Environment.GetCommandLineArgs()[0]));

                // parse all input assemblies and write the output assembly
                ParseInput(assemblies, references);
                WriteOutput(cmdLine, dllName);
            }
            catch (Exception e)
            {
                // display the error and return proper error code
#if DEBUG
                Console.Error.WriteLine(e);
#else
                Console.Error.WriteLine(e.Message);
#endif
                Environment.ExitCode = Marshal.GetHRForException(e);
            }
        }
    }
}
