﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Jint.Runtime.Debugger;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop.Reflection;
using Jint.Runtime.References;

namespace Jint
{
    public delegate JsValue MemberAccessorDelegate(Engine engine, object target, string member);

    public sealed class Options
    {
        private readonly List<IConstraint> _constraints = new();
        private bool _strict;
        private DebuggerStatementHandling _debuggerStatementHandling;
        private bool _allowClr;
        private bool _allowClrWrite = true;
        private bool _allowOperatorOverloading;
        private readonly List<IObjectConverter> _objectConverters = new();
        private Func<Engine, object, ObjectInstance> _wrapObjectHandler;
        private MemberAccessorDelegate _memberAccessor;
        private int _maxRecursionDepth = -1;
        private TimeSpan _regexTimeoutInterval = TimeSpan.FromSeconds(10);
        private CultureInfo _culture = CultureInfo.CurrentCulture;
        private TimeZoneInfo _localTimeZone = TimeZoneInfo.Local;
        private List<Assembly> _lookupAssemblies = new();
        private Predicate<Exception> _clrExceptionsHandler;
        private IReferenceResolver _referenceResolver = DefaultReferenceResolver.Instance;
        private readonly List<Action<Engine>> _configurations = new();

        private readonly List<Type> _extensionMethodClassTypes = new();
        internal ExtensionMethodCache _extensionMethods = ExtensionMethodCache.Empty;

        /// <summary>
        /// Run the script in strict mode.
        /// </summary>
        public Options Strict(bool strict = true)
        {
            _strict = strict;
            return this;
        }

        /// <summary>
        /// Selects the handling for script <code>debugger</code> statements.
        /// </summary>
        /// <remarks>
        /// The <c>debugger</c> statement can either be ignored (default) trigger debugging at CLR level (e.g. Visual Studio),
        /// or trigger a break in Jint's DebugHandler.
        /// </remarks>
        public Options DebuggerStatementHandling(DebuggerStatementHandling debuggerStatementHandling)
        {
            _debuggerStatementHandling = debuggerStatementHandling;
            return this;
        }

        /// <summary>
        /// Allow to run the script in debug mode.
        /// </summary>
        public Options DebugMode(bool debugMode = true)
        {
            IsDebugMode = debugMode;
            return this;
        }

        /// <summary>
        /// Adds a <see cref="IObjectConverter"/> instance to convert CLR types to <see cref="JsValue"/>
        /// </summary>
        public Options AddObjectConverter<T>() where T : IObjectConverter, new()
        {
            return AddObjectConverter(new T());
        }

        /// <summary>
        /// Adds a <see cref="IObjectConverter"/> instance to convert CLR types to <see cref="JsValue"/>
        /// </summary>
        public Options AddObjectConverter(IObjectConverter objectConverter)
        {
            _objectConverters.Add(objectConverter);
            return this;
        }

        public Options AddExtensionMethods(params Type[] types)
        {
            _extensionMethodClassTypes.AddRange(types);
            _extensionMethods = ExtensionMethodCache.Build(_extensionMethodClassTypes);
            return this;
        }

        private void AttachExtensionMethodsToPrototypes(Engine engine)
        {
            AttachExtensionMethodsToPrototype(engine, engine.Array.PrototypeObject, typeof(Array));
            AttachExtensionMethodsToPrototype(engine, engine.Boolean.PrototypeObject, typeof(bool));
            AttachExtensionMethodsToPrototype(engine, engine.Date.PrototypeObject, typeof(DateTime));
            AttachExtensionMethodsToPrototype(engine, engine.Number.PrototypeObject, typeof(double));
            AttachExtensionMethodsToPrototype(engine, engine.Object.PrototypeObject, typeof(ExpandoObject));
            AttachExtensionMethodsToPrototype(engine, engine.RegExp.PrototypeObject, typeof(System.Text.RegularExpressions.Regex));
            AttachExtensionMethodsToPrototype(engine, engine.String.PrototypeObject, typeof(string));
        }

        private void AttachExtensionMethodsToPrototype(Engine engine, ObjectInstance prototype, Type objectType)
        {
            if (!_extensionMethods.TryGetExtensionMethods(objectType, out var methods))
            {
                return;
            }

            foreach (var overloads in methods.GroupBy(x => x.Name))
            {

                PropertyDescriptor CreateMethodInstancePropertyDescriptor(ClrFunctionInstance clrFunctionInstance)
                {
                    var instance = clrFunctionInstance == null
                        ? new MethodInfoFunctionInstance(engine, MethodDescriptor.Build(overloads.ToList()))
                        : new MethodInfoFunctionInstance(engine, MethodDescriptor.Build(overloads.ToList()), clrFunctionInstance);

                    return new PropertyDescriptor(instance, PropertyFlag.NonConfigurable);
                }

                JsValue key = overloads.Key;
                PropertyDescriptor descriptorWithFallback = null;
                PropertyDescriptor descriptorWithoutFallback = null;

                if (prototype.HasOwnProperty(key) && prototype.GetOwnProperty(key).Value is ClrFunctionInstance clrFunctionInstance)
                {
                    descriptorWithFallback = CreateMethodInstancePropertyDescriptor(clrFunctionInstance);
                    prototype.SetOwnProperty(key, descriptorWithFallback);
                }
                else
                {
                    descriptorWithoutFallback = CreateMethodInstancePropertyDescriptor(null);
                    prototype.SetOwnProperty(key, descriptorWithoutFallback);
                }

                // make sure we register both lower case and upper case
                if (char.IsUpper(overloads.Key[0]))
                {
                    key = char.ToLower(overloads.Key[0]) + overloads.Key.Substring(1);

                    if (prototype.HasOwnProperty(key) && prototype.GetOwnProperty(key).Value is ClrFunctionInstance lowerclrFunctionInstance)
                    {
                        descriptorWithFallback = descriptorWithFallback ?? CreateMethodInstancePropertyDescriptor(lowerclrFunctionInstance);
                        prototype.SetOwnProperty(key, descriptorWithFallback);
                    }
                    else
                    {
                        descriptorWithoutFallback = descriptorWithoutFallback ?? CreateMethodInstancePropertyDescriptor(null);
                        prototype.SetOwnProperty(key, descriptorWithoutFallback);
                    }
                }
            }
        }

        /// <summary>
        /// If no known type could be guessed, objects are normally wrapped as an
        /// ObjectInstance using class ObjectWrapper. This function can be used to
        /// register a handler for a customized handling.
        /// </summary>
        public Options SetWrapObjectHandler(Func<Engine, object, ObjectInstance> wrapObjectHandler)
        {
            _wrapObjectHandler = wrapObjectHandler;
            return this;
        }

        /// <summary>
        /// Sets the type converter to use.
        /// </summary>
        public Options SetTypeConverter(Func<Engine, ITypeConverter> typeConverterFactory)
        {
            _configurations.Add(engine => engine.ClrTypeConverter = typeConverterFactory(engine));
            return this;
        }

        /// <summary>
        /// Registers a delegate that is called when CLR members are invoked. This allows
        /// to change what values are returned for specific CLR objects, or if any value 
        /// is returned at all.
        /// </summary>
        /// <param name="accessor">
        /// The delegate to invoke for each CLR member. If the delegate 
        /// returns <c>null</c>, the standard evaluation is performed.
        /// </param>
        public Options SetMemberAccessor(MemberAccessorDelegate accessor)
        {
            _memberAccessor = accessor;
            return this;
        }

        /// <summary>
        /// Allows scripts to call CLR types directly like <example>System.IO.File</example>
        /// </summary>
        public Options AllowClr(params Assembly[] assemblies)
        {
            _allowClr = true;
            _lookupAssemblies.AddRange(assemblies);
            _lookupAssemblies = _lookupAssemblies.Distinct().ToList();
            return this;
        }

        public Options AllowClrWrite(bool allow = true)
        {
            _allowClrWrite = allow;
            return this;
        }

        public Options AllowOperatorOverloading(bool allow = true)
        {
            _allowOperatorOverloading = allow;
            return this;
        }

        /// <summary>
        /// Exceptions thrown from CLR code are converted to JavaScript errors and
        /// can be used in at try/catch statement. By default these exceptions are bubbled
        /// to the CLR host and interrupt the script execution.
        /// </summary>
        public Options CatchClrExceptions()
        {
            CatchClrExceptions(_ => true);
            return this;
        }

        /// <summary>
        /// Exceptions that thrown from CLR code are converted to JavaScript errors and
        /// can be used in at try/catch statement. By default these exceptions are bubbled
        /// to the CLR host and interrupt the script execution.
        /// </summary>
        public Options CatchClrExceptions(Predicate<Exception> handler)
        {
            _clrExceptionsHandler = handler;
            return this;
        }

        public Options Constraint(IConstraint constraint)
        {
            if (constraint != null)
            {
                _constraints.Add(constraint);
            }
            return this;
        }

        public Options WithoutConstraint(Predicate<IConstraint> predicate)
        {
            _constraints.RemoveAll(predicate);
            return this;
        }

        public Options RegexTimeoutInterval(TimeSpan regexTimeoutInterval)
        {
            _regexTimeoutInterval = regexTimeoutInterval;
            return this;
        }

        /// <summary>
        /// Sets maximum allowed depth of recursion.
        /// </summary>
        /// <param name="maxRecursionDepth">
        /// The allowed depth.
        /// a) In case max depth is zero no recursion is allowed.
        /// b) In case max depth is equal to n it means that in one scope function can be called no more than n times.
        /// </param>
        /// <returns>Options instance for fluent syntax</returns>
        public Options LimitRecursion(int maxRecursionDepth = 0)
        {
            _maxRecursionDepth = maxRecursionDepth;
            return this;
        }

        public Options Culture(CultureInfo cultureInfo)
        {
            _culture = cultureInfo;
            return this;
        }

        public Options LocalTimeZone(TimeZoneInfo timeZoneInfo)
        {
            _localTimeZone = timeZoneInfo;
            return this;
        }

        public Options SetReferencesResolver(IReferenceResolver resolver)
        {
            _referenceResolver = resolver;
            return this;
        }

        /// <summary>
        /// Registers some custom logic to apply on an <see cref="Engine"/> instance when the options
        /// are loaded.
        /// </summary>
        /// <param name="configuration">The action to register.</param>
        public Options Configure(Action<Engine> configuration)
        {
            _configurations.Add(configuration);
            return this;
        }

        /// <summary>
        /// Called by the <see cref="Engine"/> instance that loads this <see cref="Options" />
        /// once it is loaded.
        /// </summary>
        internal void Apply(Engine engine)
        {
            foreach (var configuration in _configurations)
            {
                configuration?.Invoke(engine);
            }

            // add missing bits if needed
            if (_allowClr)
            {
                engine.Global.SetProperty("System", new PropertyDescriptor(new NamespaceReference(engine, "System"), PropertyFlag.AllForbidden));
                engine.Global.SetProperty("importNamespace", new PropertyDescriptor(new ClrFunctionInstance(
                    engine,
                    "importNamespace",
                    func: (thisObj, arguments) => new NamespaceReference(engine, TypeConverter.ToString(arguments.At(0)))), PropertyFlag.AllForbidden));
            }

            if (_extensionMethodClassTypes.Count > 0)
            {
                AttachExtensionMethodsToPrototypes(engine);
            }

            // ensure defaults
            engine.ClrTypeConverter ??= new DefaultTypeConverter(engine);
        }

        internal bool IsStrict => _strict;

        internal DebuggerStatementHandling _DebuggerStatementHandling => _debuggerStatementHandling;

        internal bool IsDebugMode { get; private set; }

        internal bool _IsClrWriteAllowed => _allowClrWrite;

        internal bool _IsOperatorOverloadingAllowed => _allowOperatorOverloading;

        internal Predicate<Exception> _ClrExceptionsHandler => _clrExceptionsHandler;

        internal List<Assembly> _LookupAssemblies => _lookupAssemblies;

        internal List<IObjectConverter> _ObjectConverters => _objectConverters;

        internal List<IConstraint> _Constraints => _constraints;

        internal Func<Engine, object, ObjectInstance> _WrapObjectHandler => _wrapObjectHandler;
        internal MemberAccessorDelegate _MemberAccessor => _memberAccessor;

        internal int MaxRecursionDepth => _maxRecursionDepth;

        internal TimeSpan _RegexTimeoutInterval => _regexTimeoutInterval;

        internal CultureInfo _Culture => _culture;

        internal TimeZoneInfo _LocalTimeZone => _localTimeZone;

        internal IReferenceResolver ReferenceResolver => _referenceResolver;

        private sealed class DefaultReferenceResolver : IReferenceResolver
        {
            public static readonly DefaultReferenceResolver Instance = new DefaultReferenceResolver();

            private DefaultReferenceResolver()
            {
            }

            public bool TryUnresolvableReference(Engine engine, Reference reference, out JsValue value)
            {
                value = JsValue.Undefined;
                return false;
            }

            public bool TryPropertyReference(Engine engine, Reference reference, ref JsValue value)
            {
                return false;
            }

            public bool TryGetCallable(Engine engine, object callee, out JsValue value)
            {
                value = JsValue.Undefined;
                return false;
            }

            public bool CheckCoercible(JsValue value)
            {
                return false;
            }
        }
    }
}
