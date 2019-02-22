// This file is part of Core WF which is licensed under the MIT license.
// See LICENSE file in the project root for full license information.

namespace System.Activities.Debugger
{
    using System;
    using System.Activities.XamlIntegration;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Runtime;
    using Portable.Xaml.Markup;
    using Portable.Xaml;
    using System.Activities.Internals;
    using System.Activities.Runtime;

    // Class to communicate with Workflow's Expression Evaluation.
    // The methods of this class get invoked thru reflection by Visual Studio, so this needs to be public
    // to allow partial trust to work.
    public class DebugInfo
    {
        ActivityInstance activityInstance;
        LocalInfo[] locals;
        LocalInfo[] arguments;
        Dictionary<string, LocalInfo> cachedLocalInfos;

        internal DebugInfo(ActivityInstance activityInstance)
        {
            Fx.Assert(activityInstance != null, "activityInstance cannot be null");
            this.activityInstance = activityInstance;
        }

        internal object EvaluateExpression(string expressionString)
        {
            // This is to shortcircuit a common case where 
            // internally, vsdebugger calls our EE with literal "0"            
            int intResult;
            if (int.TryParse(expressionString, out intResult))
            {
                return intResult;
            }

            // At refresh, Expression Evaluator may re-evaluate locals/arguments.
            // To speed up the process, locals/arguments are cached.
            LocalInfo cachedLocalInfo = null;
            if (this.cachedLocalInfos != null && this.cachedLocalInfos.TryGetValue(expressionString, out cachedLocalInfo))
            {
                return cachedLocalInfo;
            }

            Activity activity = activityInstance.Activity;
            LocationReferenceEnvironment locationReferenceEnvironment = activity.PublicEnvironment;

            // a temporary context for EvaluateExpression
            // We're not using the executor so it's ok that it's null
            CodeActivityContext context = new CodeActivityContext(activityInstance, null);

            object result;
            try
            {
                // First try as R-Value
                if (!TryEvaluateExpression(expressionString, null, locationReferenceEnvironment, context, out result))
                {
                    return SR.DebugInfoCannotEvaluateExpression(expressionString);
                }
            }
            catch (Exception ex)
            {
                // Swallow all exceptions, this exception is generated by user typing input in either
                // Watch window or Immediate windows.  Exception should not affect the current runtime.
                // Except for fatal exception.
                if (Fx.IsFatal(ex))
                {
                    throw;
                }
                context.Dispose();
                return SR.DebugInfoCannotEvaluateExpression(expressionString);
            }

            // Now try expression as an L-Value if possible.
            try
            {
                object resultLocation;
                if (TryEvaluateExpression(expressionString, result.GetType(), locationReferenceEnvironment, context, out resultLocation))
                {
                    LocalInfo localInfo = new LocalInfo()
                                            {
                                                Name = expressionString,
                                                Location = resultLocation as Location
                                            };
                    this.cachedLocalInfos[expressionString] = localInfo;
                    return localInfo;
                }
            }
            catch (Exception ex)
            {
                // Swallow all exceptions, this exception is generated by user typing input in either
                // Watch window or Immediate windows.  Exception should not affect the current runtime.
                // Except for fatal exception.
                if (Fx.IsFatal(ex))
                {
                    throw;
                }
            }
            finally
            {
                context.Dispose();
            }
            return result;
        }

        static bool TryEvaluateExpression(
            string expressionString,
            Type locationValueType,                             // Non null for Reference type (location)
            LocationReferenceEnvironment locationReferenceEnvironment,
            CodeActivityContext context,
            out object result)
        {
            expressionString = string.Format(CultureInfo.InvariantCulture, "[{0}]", expressionString);

            Type activityType;
            if (locationValueType != null)
            {
                activityType = typeof(Activity<>).MakeGenericType(typeof(Location<>).MakeGenericType(locationValueType));
            }
            else
            {
                activityType = typeof(Activity<object>);
            }

            // General expression.
            ActivityWithResultConverter converter = new ActivityWithResultConverter(activityType);
            ActivityWithResult expression = converter.ConvertFromString(
                new TypeDescriptorContext { LocationReferenceEnvironment = locationReferenceEnvironment },
                expressionString) as ActivityWithResult;

            if (locationValueType != null)
            {
                Type locationHelperType = typeof(LocationHelper<>).MakeGenericType(locationValueType);
                LocationHelper helper = (LocationHelper)Activator.CreateInstance(locationHelperType);
                return helper.TryGetValue(expression, locationReferenceEnvironment, context, out result);
            }
            else
            {
                return TryEvaluateExpression(expression, locationReferenceEnvironment, context, out result);
            }
        }

        static bool TryEvaluateExpression(
            ActivityWithResult element,
            LocationReferenceEnvironment locationReferenceEnvironment,
            CodeActivityContext context,
            out object result)
        {
            // value is some expression type and needs to be opened
            context.Reinitialize(context.CurrentInstance, context.CurrentExecutor, element, context.CurrentInstance.InternalId);
            if (element != null && !element.IsRuntimeReady)
            {
                WorkflowInspectionServices.CacheMetadata(element, locationReferenceEnvironment);
            }

            if (element == null || !element.IsFastPath)
            {
                result = SR.DebugInfoNotSkipArgumentResolution;
                return false;
            }

            result = element.InternalExecuteInResolutionContextUntyped(context);
            return true;
        }

        internal LocalInfo[] GetArguments()
        {
            if (this.arguments == null || this.arguments.Length == 0)
            {
                this.arguments =
                    activityInstance.Activity.RuntimeArguments.Select(argument =>
                        new LocalInfo
                        {
                            Name = argument.Name,
                            Location = argument.InternalGetLocation(activityInstance.Environment)
                        }).ToArray();

                if (this.arguments.Length > 0)
                {
                    this.CacheLocalInfos(this.arguments);
                }
            }
            return this.arguments;
        }

        internal LocalInfo[] GetLocals()
        {
            if (this.locals == null || this.locals.Length == 0)
            {
                Activity activity = activityInstance.Activity;
                List<Variable> allVariables = new List<Variable>();
                List<RuntimeArgument> allArguments = new List<RuntimeArgument>();
                List<DelegateArgument> allDelegateArguments = new List<DelegateArgument>();

                HashSet<string> existingNames = new HashSet<string>();
                while (activity != null)
                {
                    allVariables.AddRange(RemoveHiddenVariables(existingNames, activity.RuntimeVariables));
                    allVariables.AddRange(RemoveHiddenVariables(existingNames, activity.ImplementationVariables));
                    if (activity.HandlerOf != null)
                    {
                        allDelegateArguments.AddRange(RemoveHiddenDelegateArguments(existingNames,
                           activity.HandlerOf.RuntimeDelegateArguments.Select(delegateArgument =>
                                delegateArgument.BoundArgument)));
                    }
                    allArguments.AddRange(RemoveHiddenArguments(existingNames, activity.RuntimeArguments));
                    activity = activity.Parent;
                }

                this.locals =
                    new LocalInfo[] {
                                new LocalInfo
                                {
                                    Name = "this",
                                    Type = "System.Activities.ActivityInstance",
                                    Value = activityInstance
                                }
                            }
                        .Concat(allVariables.Select(variable =>
                            new LocalInfo
                            {
                                Name = variable.Name,
                                Location = variable.InternalGetLocation(activityInstance.Environment)
                            })
                        .Concat(allArguments.Select(argument =>
                            new LocalInfo
                            {
                                Name = argument.Name,
                                Location = argument.InternalGetLocation(activityInstance.Environment)
                            }))
                        .Concat(allDelegateArguments.Select(argument =>
                            new LocalInfo
                            {
                                Name = argument.Name,
                                Location = argument.InternalGetLocation(activityInstance.Environment)
                            }))
                        .OrderBy(info => info.Name))
                        .ToArray();

                if (this.locals.Length > 0)
                {
                    this.CacheLocalInfos(this.locals);
                }
            }
            return this.locals;
        }

        // Remove ancestor's variables that are hidden because the same name already define in the current scope.
        // This will also update existingNames to include ancestor variable names that are retained.
        static List<Variable> RemoveHiddenVariables(HashSet<string> existingNames, IEnumerable<Variable> ancestorVariables)
        {
            List<Variable> cleanUpList = new List<Variable>();
            foreach (Variable variable in ancestorVariables)
            {
                if (variable.Name == null)
                {
                    continue;
                }

                if (!(variable.Name.StartsWith("_", StringComparison.Ordinal) ||                  // private variables that should be hidden
                        existingNames.Contains(variable.Name)))         // variable name already exists in current scope
                {
                    cleanUpList.Add(variable);
                    existingNames.Add(variable.Name);
                }
            }
            return cleanUpList;
        }

        // Remove ancestor's arguments that are hidden because the same name already define in the current scope.
        // This will also update existingNames to include ancestor delegate argument names that are retained.
        static List<DelegateArgument> RemoveHiddenDelegateArguments(HashSet<string> existingNames, IEnumerable<DelegateArgument> ancestorDelegateArguments)
        {
            List<DelegateArgument> cleanUpList = new List<DelegateArgument>();
            foreach (DelegateArgument delegateArgument in ancestorDelegateArguments)
            {
                if (delegateArgument != null && delegateArgument.Name != null)
                {
                    if (!existingNames.Contains(delegateArgument.Name)) // variable name already exists in current scope
                    {
                        cleanUpList.Add(delegateArgument);
                        existingNames.Add(delegateArgument.Name);
                    }
                }
            }
            return cleanUpList;
        }

        // Remove ancestor's arguments that are hidden because the same name already define in the current scope.
        // This will also update existingNames to include ancestor argument names that are retained.
        static List<RuntimeArgument> RemoveHiddenArguments(HashSet<string> existingNames, IList<RuntimeArgument> ancestorArguments)
        {
            List<RuntimeArgument> cleanUpList = new List<RuntimeArgument>(ancestorArguments.Count);
            foreach (RuntimeArgument argument in ancestorArguments)
            {
                if (!existingNames.Contains(argument.Name))
                {
                    cleanUpList.Add(argument);
                    existingNames.Add(argument.Name);
                }
            }
            return cleanUpList;
        }

        internal bool SetValueAsString(Location location, string value, string stringRadix)
        {
            bool succeed = true;
            try
            {
                value = value.Trim();

                Type t = location.LocationType;

                if (t == typeof(string) && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                {
                    location.Value = RemoveQuotes(value);
                }
                else if (t == typeof(bool))
                {
                    location.Value = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                }
                else if (t == typeof(sbyte))
                {
                    location.Value = Convert.ToSByte(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(char))
                {
                    char ch;
                    succeed = ConvertToChar(value, Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture), out ch);
                    if (succeed)
                    {
                        location.Value = ch;
                    }
                }
                else if (t == typeof(Int16))
                {
                    location.Value = Convert.ToInt16(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(Int32))
                {
                    location.Value = Convert.ToInt32(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(Int64))
                {
                    location.Value = Convert.ToInt64(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(byte))
                {
                    location.Value = Convert.ToByte(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(UInt16))
                {
                    location.Value = Convert.ToUInt16(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(UInt32))
                {
                    location.Value = Convert.ToUInt32(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(UInt64))
                {
                    location.Value = Convert.ToUInt64(RemoveHexadecimalPrefix(value), Convert.ToInt32(stringRadix, CultureInfo.InvariantCulture));
                }
                else if (t == typeof(Single))
                {
                    if (!value.Contains(","))
                    {
                        location.Value = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        succeed = false;
                    }
                }
                else if (t == typeof(Double))
                {
                    if (!value.Contains(","))
                    {
                        location.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        succeed = false;
                    }
                }
                else if (t == typeof(Decimal))
                {
                    value = value.TrimEnd();
                    if (value.EndsWith("M", StringComparison.OrdinalIgnoreCase) ||  // suffix for Decimal format in C#
                        value.EndsWith("D", StringComparison.OrdinalIgnoreCase))    // suffix for Decimal format in VB
                    {
                        value = value.Substring(0, value.Length - 1);   // remove the suffix
                    }
                    if (value.Contains(","))
                    {
                        succeed = false;
                    }
                    else
                    {
                        location.Value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    }
                }
                else if (t == typeof(DateTime))
                {
                    location.Value = Convert.ToDateTime(value, CultureInfo.CurrentCulture);
                }
                else if (t.IsEnum)
                {
                    location.Value = Enum.Parse(t, value, true);
                }
            }
            catch (InvalidCastException)
            {
                succeed = false;
            }
            catch (OverflowException)
            {
                succeed = false;
            }
            catch (FormatException)
            {
                succeed = false;
            }
            catch (ArgumentOutOfRangeException)
            {
                succeed = false;
            }
            return succeed;
        }

        // Cache local infos for faster evaluation.
        void CacheLocalInfos(LocalInfo[] localInfos)
        {
            if (this.cachedLocalInfos == null)
            {
                this.cachedLocalInfos = new Dictionary<string, LocalInfo>(StringComparer.OrdinalIgnoreCase);
            }
            foreach (LocalInfo localInfo in localInfos)
            {
                this.cachedLocalInfos[localInfo.Name] = localInfo;
            }
        }

        static string RemoveHexadecimalPrefix(string stringValue)
        {
            stringValue = stringValue.Trim().ToUpperInvariant();
            if (stringValue.StartsWith("0X", StringComparison.Ordinal))
            {
                stringValue = stringValue.Substring(2);
            }
            return stringValue;
        }

        static string RemoveQuotes(string stringValue)
        {
            if (stringValue.StartsWith("\"", StringComparison.Ordinal))
            {
                stringValue = stringValue.Substring(1);
            }
            if (stringValue.EndsWith("\"", StringComparison.Ordinal))
            {
                stringValue = stringValue.Substring(0, stringValue.Length - 1);
            }
            return stringValue;
        }

        static bool ConvertToChar(string stringValue, int radix, out char ch)
        {
            bool succeed = false;
            ch = '\0';  // null character
            try
            {
                if ((stringValue[0] == '\'') || (stringValue[0] == '"'))  // VB Char is using double quote
                {
                    if (stringValue[1] == '\\')
                    {
                        switch (stringValue[2])
                        {
                            case '\'':  // single quote
                            case 'a':
                            case 'b':
                            case 'f':
                            case 'n':
                            case 'r':
                            case 't':
                            case 'v':
                                if (stringValue[3] == stringValue[0])     // matched single/double quote
                                {
                                    ch = stringValue[2];
                                }
                                succeed = true;
                                break;
                        }
                    }
                    else if (stringValue[2] == stringValue[0])  // matched single/double quote
                    {
                        ch = stringValue[1];
                        succeed = true;
                    }
                }
                else
                {   // It can be either an digit , e.g. 65, or 65'A'.
                    // For the second case, we ignore the char.
                    int endIndex = stringValue.IndexOf('\'');
                    if (endIndex < 0)
                    {
                        endIndex = stringValue.IndexOf('"');
                    }
                    if (endIndex > 0)
                    {
                        stringValue = stringValue.Substring(0, endIndex);
                    }
                    ch = (char)Convert.ToUInt16(RemoveHexadecimalPrefix(stringValue), radix);
                    succeed = true;
                }
            }
            catch (IndexOutOfRangeException)
            {   // Invalid character length
                succeed = false;
            }
            return succeed;
        }


        //[SuppressMessage(FxCop.Category.Design, FxCop.Rule.NestedTypesShouldNotBeVisible, Justification = "Needed for partial trust.")]
        internal class LocalInfo
        {
            string type;
            object valueField;
            //[SuppressMessage(FxCop.Category.Design, FxCop.Rule.DoNotDeclareVisibleInstanceFields, Justification = "Needed for partial trust.")]
            public string Name;
            //[SuppressMessage(FxCop.Category.Design, FxCop.Rule.DoNotDeclareVisibleInstanceFields, Justification = "Needed for partial trust.")]
            public Location Location;

            //[SuppressMessage(FxCop.Category.Performance, FxCop.Rule.AvoidUncalledPrivateCode, Justification = "Called by Expression Evaluator")]
            public object Value
            {
                get
                {
                    if (this.Location != null)
                    {
                        return this.Location.Value;
                    }
                    return this.valueField;
                }
                set
                {
                    this.valueField = value;
                }
            }

            //[SuppressMessage(FxCop.Category.Performance, FxCop.Rule.AvoidUncalledPrivateCode, Justification = "Called by Expression Evaluator")]
            public string Type
            {
                get
                {
                    if (this.Location != null)
                    {
                        return this.Location.LocationType.Name;
                    }
                    return this.type;
                }
                set
                {
                    this.type = value;
                }

            }


        }


        class TypeDescriptorContext : ITypeDescriptorContext, IXamlNamespaceResolver, INameScope, INamespacePrefixLookup
        {
            public LocationReferenceEnvironment LocationReferenceEnvironment;

            public IContainer Container
            {
                get { throw FxTrace.Exception.AsError(new NotImplementedException()); }
            }

            public object Instance
            {
                get { throw FxTrace.Exception.AsError(new NotImplementedException()); }
            }

            public PropertyDescriptor PropertyDescriptor
            {
                get { throw FxTrace.Exception.AsError(new NotImplementedException()); }
            }


            public void OnComponentChanged()
            {
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public bool OnComponentChanging()
            {
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public object GetService(Type serviceType)
            {
                if (serviceType.IsAssignableFrom(typeof(TypeDescriptorContext)))
                {
                    return this;
                }
                return null;
            }


            public string GetNamespace(string prefix)
            {
                if (string.IsNullOrEmpty(prefix))
                {
                    return String.Empty;
                }
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public object FindName(string name)
            {
                LocationReference result;
                if (LocationReferenceEnvironment.TryGetLocationReference(name, out result))
                {
                    return result;
                }

                throw FxTrace.Exception.AsError(new InvalidOperationException(SR.VariableOrArgumentDoesNotExist(name)));
            }

            public void RegisterName(string name, object scopedElement)
            {
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public void UnregisterName(string name)
            {
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public string LookupPrefix(string name)
            {
                throw FxTrace.Exception.AsError(new NotImplementedException());
            }

            public IEnumerable<NamespaceDeclaration> GetNamespacePrefixes()
            {
                return Enumerable.Empty<NamespaceDeclaration>();
            }
        }

        // to perform the generics dance around Locations we need these helpers
        abstract class LocationHelper
        {
            public abstract bool TryGetValue(Activity expression, LocationReferenceEnvironment locationReferenceEnvironment, CodeActivityContext context, out object result);
        }

        class LocationHelper<TLocationValue> : LocationHelper
        {
            public override bool TryGetValue(Activity expression, LocationReferenceEnvironment locationReferenceEnvironment, CodeActivityContext context, out object result)
            {
                Activity<Location<TLocationValue>> activity = expression as Activity<Location<TLocationValue>>;
                result = null;
                if (activity != null)
                {
                    context.Reinitialize(context.CurrentInstance, context.CurrentExecutor, expression, context.CurrentInstance.InternalId);
                    if (activity != null && !activity.IsRuntimeReady)
                    {
                        WorkflowInspectionServices.CacheMetadata(activity, locationReferenceEnvironment);
                    }
                    if (activity.IsFastPath)
                    {
                        result = activity.InternalExecuteInResolutionContext(context);
                        return true;
                    }
                }
                return false;
            }
        }
    }
}