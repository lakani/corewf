// This file is part of Core WF which is licensed under the MIT license.
// See LICENSE file in the project root for full license information.

namespace System.Activities.XamlIntegration
{
    using System;
    using Portable.Xaml.Markup;
    using Portable.Xaml;
    using System.Activities.Internals;

    public sealed class ActivityWithResultValueSerializer : ValueSerializer
    {
        private static ActivityWithResultValueSerializer valueSerializer;

        public override bool CanConvertToString(object value, IValueSerializerContext context)
        {
            if (AttachablePropertyServices.GetAttachedPropertyCount(value) > 0)
            {
                return false;
            }
            else if (value != null && 
                value is IValueSerializableExpression && 
                ((IValueSerializableExpression)value).CanConvertToString(context))
            {
                return true;
            }

            return false;
        }

        public override string ConvertToString(object value, IValueSerializerContext context)
        {
            IValueSerializableExpression ivsExpr;

            ivsExpr = value as IValueSerializableExpression;
            if (ivsExpr == null)
            {
                throw FxTrace.Exception.AsError(new InvalidOperationException(SR.CannotSerializeExpression(value.GetType())));
            }
            return ivsExpr.ConvertToString(context);
        }

        internal static bool CanConvertToStringWrapper(object value, IValueSerializerContext context)
        {
            if (valueSerializer == null)
            {
                valueSerializer = new ActivityWithResultValueSerializer();
            }

            return valueSerializer.CanConvertToString(value, context);
        }

        internal static string ConvertToStringWrapper(object value, IValueSerializerContext context)
        {
            if (valueSerializer == null)
            {
                valueSerializer = new ActivityWithResultValueSerializer();
            }

            return valueSerializer.ConvertToString(value, context);
        }
    }
}
