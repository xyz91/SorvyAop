﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace Sorvy
{
    public class SorvyAop
    {
        public static void Work(string dllFilePath, string outpath)
        {
            try
            {
                using (AssemblyDefinition ass = AssemblyDefinition.ReadAssembly(dllFilePath))
                {
                    foreach (var type in ass.MainModule.Types)
                    {
                        if (null != outpath)
                        {
                            type.Module.AssemblyResolver.AddSearchDirectory(outpath);
                        }                        
                        List<MethodDefinition> methods = new List<MethodDefinition>();
                        List<CustomAttribute> typeatts = type.CustomAttributes.Where(a => CheckAttribute(a.AttributeType)).ToList();
                        typeatts.ForEach(a=>type.CustomAttributes.Remove(a));
                        List<PropertyDefinition> properties = new List<PropertyDefinition>();
                        foreach (var method in type.Methods)
                        {
                            IEnumerable<CustomAttribute> methodatts = method.CustomAttributes.Where(a => CheckAttribute(a.AttributeType));
                            
                            var newatts = new List<CustomAttribute>();
                            if (method.IsConstructor)
                            {
                                var ctor = FilterAttribute(typeatts, AopType.Ctor, false);
                                newatts.AddRange(ctor);                               
                            }
                            else if (method.IsGetter)
                            {
                                var getpro = type.Properties.SingleOrDefault(a => a.Name == method.Name.Substring(4));
                                if (getpro != null)
                                {
                                    var getatts = getpro.CustomAttributes.Where(a => CheckAttribute(a.AttributeType));
                                    newatts.AddRange(FilterAttribute(getatts, AopType.Get, true));
                                    if (!properties.Contains(getpro))
                                    {
                                        properties.Add(getpro);
                                    }
                                }
                                newatts.AddRange(FilterAttribute(typeatts, AopType.Get, false));
                            }
                            else if (method.IsSetter)
                            {
                                var setpro = type.Properties.SingleOrDefault(a => a.Name == method.Name.Substring(4));
                                if (setpro != null)
                                {
                                    var setatts = setpro.CustomAttributes.Where(a => CheckAttribute(a.AttributeType));
                                    newatts.AddRange(FilterAttribute(setatts, AopType.Set, true));
                                    if (!properties.Contains(setpro))
                                    {
                                        properties.Add(setpro);
                                    }
                                }
                                newatts.AddRange(FilterAttribute(typeatts, AopType.Set, false));
                            }
                            else
                            {
                                newatts.AddRange(FilterAttribute(typeatts, AopType.Method, true));
                            }
                            properties.ForEach(a=> {
                                a.CustomAttributes.Where(b => CheckAttribute(b.AttributeType)).ToList().ForEach(c => a.CustomAttributes.Remove(c));
                            });
                            newatts.AddRange(methodatts);
                            methodatts.ToList().ForEach(a => method.CustomAttributes.Remove(a));
                            newatts = newatts.OrderBy(a =>
                            {
                                var op = a.Properties.SingleOrDefault(b => b.Name == "Order");
                                var opv = op.Argument.Value;
                                return Convert.ToInt32(opv);
                            }).ToList();
                            if (newatts.Count > 0)
                            {
                                var m = EditMethod(method, newatts);
                                if (m != null)
                                {
                                    methods.Add(m);
                                }
                            }
                        }
                        methods.ForEach(a => type.Methods.Add(a));
                    }
                    ass.Write(dllFilePath + ".temp");
                }
                File.Copy(dllFilePath, dllFilePath + ".old", true);
                File.Delete(dllFilePath);
                File.Copy(dllFilePath + ".temp", dllFilePath, true);
                File.Delete(dllFilePath + ".temp");
            }
            catch (Exception e) { throw new Exception("result:" + e.Message + e.StackTrace); }
        }

        private static bool CheckAttribute(TypeReference type)
        {
            if (type.Name == "BaseAop")
            {
                return true;
            }
            else if (type.Name == "Attribute")
            {
                return false;
            }
            else
            {
                return CheckAttribute(type.Resolve().BaseType);
            }
        }
        private static IEnumerable<CustomAttribute> FilterAttribute(IEnumerable<CustomAttribute> attributes, AopType type, bool isDefault)
        {
            return attributes.Where(a =>
            {
                var op = a.Properties.SingleOrDefault(b => b.Name == "Type");
                if (null == op.Name )
                {
                    return isDefault || false;
                }
                var opv = op.Argument.Value;
                int i = Convert.ToInt32(opv);
                AopType aop = ((AopType)i) & type;
                return aop == type;
            });
        }

        private static MethodDefinition EditMethod(MethodDefinition method, List<CustomAttribute> atts)
        {
            if (null == method  ||null == atts  || atts.Count == 0)
            {
                return null;
            }
            atts = atts.Distinct().ToList();
            ILProcessor il = method.Body.GetILProcessor();
            MethodDefinition newmethod = method.Clone();
            method.Body.Instructions.Clear();
            atts.ForEach(a => method.CustomAttributes.Remove(a));
            if (!method.IsConstructor)
            {
                method.Body.Variables.Clear();
                method.Body.ExceptionHandlers.Clear();
            }
            var methbase = new VariableDefinition(method.Module.ImportReference(typeof(ExceEventArg)));
            method.Body.Variables.Add(methbase);

            var exception = new VariableDefinition(method.Module.ImportReference(typeof(System.Exception)));
            method.Body.Variables.Add(exception);

            var ps = new VariableDefinition(method.Module.ImportReference(typeof(List<object>)));
            method.Body.Variables.Add(ps);

            VariableDefinition returnvalue = null;

            var returnvoid = method.ReturnType.FullName == "System.Void";
            if (!returnvoid)
            {
                returnvalue = new VariableDefinition(method.Module.ImportReference(method.ReturnType));
                method.Body.Variables.Add(returnvalue);
            }

            var curmethod = method.Module.ImportReference(typeof(MethodBase).GetMethod("GetCurrentMethod"));

            il.AppendArr(new[] {
                                il.Create(OpCodes.Newobj, method.Module.ImportReference(typeof(ExceEventArg).GetConstructor(new Type[] { }))),
                                il.Create(OpCodes.Stloc_S,methbase),
                                il.Create(OpCodes.Ldloc_S,methbase),
                                il.Create(OpCodes.Call, curmethod),
                                il.Create(OpCodes.Callvirt,method.Module.CreateMethod<ExceEventArg>("set_methodBase",typeof(MethodBase))),
                        });

            if (method.Parameters.Count > 0)
            {
                il.AppendArr(new[] {
                                il.Create(OpCodes.Newobj,method.Module.ImportReference(typeof(List<object>).GetConstructor(new Type[]{ }))),
                                il.Create(OpCodes.Stloc_S,ps),
                            });
                foreach (var p in method.Parameters)
                {
                    il.Append(il.Create(OpCodes.Ldloc_S, ps));
                    il.Append(il.Create(OpCodes.Ldarg_S, p));
                    il.Append(il.Create(OpCodes.Box, method.Module.ImportReference(p.ParameterType)));
                    il.Append(il.Create(OpCodes.Call, method.Module.CreateMethod<List<object>>("Add", typeof(object))));
                }

                il.AppendArr(new[] {
                                il.Create(OpCodes.Ldloc_S, methbase),
                                il.Create(OpCodes.Ldloc_S, ps),
                                il.Create(OpCodes.Callvirt, method.Module.CreateMethod<ExceEventArg>("set_parameters", typeof(List<object>))),
                        });
            }

            List<TypeDefinition> typeDefinitions = new List<TypeDefinition>();
            List<VariableDefinition> variables = new List<VariableDefinition>();
            Dictionary<CustomAttribute, Dictionary<string, Instruction>> excehandler = new Dictionary<CustomAttribute, Dictionary<string, Instruction>>();
            for (int i = 0; i < atts.Count(); i++)
            {
                var excedic = new Dictionary<string, Instruction>
                {
                    { "TryStart", il.Create(OpCodes.Nop) },
                    { "TryEnd", il.Create(OpCodes.Stloc_S, exception) },
                    { "HandlerStart", il.Create(OpCodes.Nop) },
                    { "HandlerEnd", il.Create(OpCodes.Nop) }
                };
                excehandler.Add(atts[i], excedic);
                var w = new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    CatchType = method.Module.ImportReference(typeof(Exception)),
                    TryStart = excehandler[atts[i]]["TryStart"],
                    TryEnd = excehandler[atts[i]]["TryEnd"],
                    HandlerStart = excehandler[atts[i]]["TryEnd"],
                    HandlerEnd = excehandler[atts[i]]["HandlerEnd"]
                };
                method.Body.ExceptionHandlers.Add(w);
                TypeDefinition re = atts[i].AttributeType.Resolve();
                typeDefinitions.Add(re);            
                var begin = SearchMethod(re, "Before");
               
                var log = new VariableDefinition(method.Module.ImportReference(atts[i].AttributeType));
                method.Body.Variables.Add(log);
                variables.Add(log);
                il.AppendArr(new[] {
                                il.Create(OpCodes.Newobj, atts[i].Constructor),
                                il.Create(OpCodes.Stloc_S,log),
                        });
                if (begin != null)
                {
                    il.AppendArr(new[] {
                                il.Create(OpCodes.Ldloc_S,log),
                                il.Create(OpCodes.Ldloc_S,methbase),
                                il.Create(OpCodes.Call,begin),
                        });
                }
            }
            for (int i = atts.Count - 1; i >= 0; i--)
            {
                il.Append(excehandler[atts[i]]["TryStart"]);
            }
            if (method.IsConstructor)
            {
                newmethod.Body.Instructions.RemoveAt(newmethod.Body.Instructions.Count - 1);
                il.AppendArr(newmethod.Body.Instructions.ToArray());
            }
            else
            {
                if (!method.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }
                foreach (var p in method.Parameters)
                {
                    il.Append(il.Create(OpCodes.Ldarg_S, p));
                }
                il.Append(il.Create(OpCodes.Call, newmethod));
            }
            if (!returnvoid)
            {
                il.AppendArr(new[] {
                                il.Create(OpCodes.Stloc_S, returnvalue),
                            il.Create(OpCodes.Ldloc_S, methbase),
                            il.Create(OpCodes.Ldloc_S, returnvalue),
                            il.Create(OpCodes.Box,method.Module.ImportReference(method.ReturnType)),
                            il.Create(OpCodes.Callvirt,method.Module.CreateMethod<ExceEventArg>("set_returnValue",method.ReturnType.GetType())),
                            });
            }
            for (int i = 0; i < atts.Count(); i++)
            {               
                var exce = SearchMethod(typeDefinitions[i], "Exception");
                il.AppendArr(new[] {

                             il.Create(OpCodes.Leave_S,excehandler[atts[i]]["HandlerEnd"]),
                            excehandler[atts[i]]["TryEnd"],
                            il.Create(OpCodes.Nop),

                            il.Create(OpCodes.Ldloc_S,methbase),
                            il.Create(OpCodes.Ldloc_S,exception),

                            il.Create(OpCodes.Callvirt,method.Module.CreateMethod<ExceEventArg>("set_exception",typeof(Exception))),

                            il.Create(OpCodes.Ldloc_S,variables[i]),
                                il.Create(OpCodes.Ldloc_S,methbase),
                                il.Create(OpCodes.Call,exce),

                            il.Create(OpCodes.Nop),
                            il.Create(OpCodes.Leave_S,excehandler[atts[i]]["HandlerEnd"]),
                            excehandler[atts[i]]["HandlerEnd"],
                        });

                var after = SearchMethod(typeDefinitions[i], "After");

                if (after != null)
                {
                    il.AppendArr(new[] {
                            il.Create(OpCodes.Ldloc_S,variables[i]),
                                il.Create(OpCodes.Ldloc_S,methbase),
                                il.Create(OpCodes.Call,after),
                        });
                }
            }
            if (!returnvoid)
            {
                il.Append(il.Create(OpCodes.Ldloc_S, returnvalue));
            }
            il.Append(il.Create(OpCodes.Ret));
            if (method.IsConstructor)
            {
                return null;
            }
            return newmethod;
        }
        private static MethodDefinition SearchMethod(TypeDefinition definition, string name)
        {

            var method = definition.Methods.FirstOrDefault(a => a.Name == name);
            if (null == method)
            {
                return SearchMethod(definition.BaseType.Resolve(), name);
            }
            return method;
        }
    }
    static class MonoExtended
    {
        public static void AppendArr(this ILProcessor il, Instruction[] ins)
        {
            foreach (var item in ins)
            {
                il.Append(item);
            }
        }
        public static MethodReference CreateMethod<T>(this ModuleDefinition module, string methodName, params Type[] types)
        {
            if (null == types )
            {
                types = new Type[] { };
            }
            return module.ImportReference(typeof(T).GetMethod(methodName, types));
        }
        public static MethodDefinition Clone(this MethodDefinition method)
        {
            var newmethod = new MethodDefinition(method.Name + Guid.NewGuid().ToString("N"), method.Attributes, method.ReturnType);
            method.Parameters.ToList().ForEach(a => { newmethod.Parameters.Add(a); });
            method.GenericParameters.ToList().ForEach(a => { newmethod.GenericParameters.Add(a); });
            //method.CustomAttributes.ToList().ForEach(a => { newmethod.CustomAttributes.Add(a); });
            method.Body.Instructions.ToList().ForEach(a => { newmethod.Body.Instructions.Add(a); });
            method.Body.Variables.ToList().ForEach(a => { newmethod.Body.Variables.Add(a); });
            method.Body.ExceptionHandlers.ToList().ForEach(a => { newmethod.Body.ExceptionHandlers.Add(a); });
            newmethod.Body.InitLocals = method.Body.InitLocals;
            newmethod.Body.LocalVarToken = method.Body.LocalVarToken;
            newmethod.IsPrivate = true;
            newmethod.IsStatic = method.IsStatic;
            return newmethod;
        }
    }
    [AttributeUsageAttribute(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor, AllowMultiple = false)]
    public abstract class BaseAop : Attribute
    {
        public int Order { get; set; }
        public AopType Type { get; set; }
        public abstract void Before(ExceEventArg method);
        public abstract void After(ExceEventArg method);
        public abstract void Exception(ExceEventArg method);
        
    }
    /// <summary>
    /// use in class or property, it don't work in method
    /// </summary>
    public enum AopType
    {
        /// <summary>
        /// if on class then method ,if on property then get and set
        /// </summary>
        Undefined = 0,
        /// <summary>
        /// only method 
        /// </summary>
        Method = 1,
        /// <summary>
        /// only ctor
        /// </summary>
        Ctor = 2,
        /// <summary>
        /// only get
        /// </summary>
        Get = 4,
        /// <summary>
        /// only set
        /// </summary>
        Set = 8,
        /// <summary>
        /// get or set 
        /// </summary>
        Property = 12,
        /// <summary>
        /// all
        /// </summary>
        All = 15
    }
    public class ExceEventArg
    {
        public Exception exception { get; set; }
        public MethodBase methodBase { get; set; }
        public object returnValue { get; set; }
        private List<object> _parameters = new List<object>();
        public List<object> parameters { get { return _parameters; } set { _parameters = value; } }
    }

    public class SorvyAopTask : Microsoft.Build.Utilities.Task
    {
        public string OutputPath
        {
            get; set;
        }
        public string TargetPath { get; set; }
        public override bool Execute()
        {
            try
            {
                SorvyAop.Work(TargetPath, OutputPath);
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("任务出错:" + e.Message);
                return false;
            }

        }
    }
}
