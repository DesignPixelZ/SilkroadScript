﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace LiteDB
{
    /// <summary>
    ///     Helper class to get entity properties and map as BsonValue
    /// </summary>
    internal class Reflection
    {
        private static readonly Dictionary<Type, CreateObject> _cacheCtor = new Dictionary<Type, CreateObject>();

        #region GetProperties

        /// <summary>
        ///     Read all properties from a type - store in a static cache - exclude: Id and [BsonIgnore]
        /// </summary>
        public static Dictionary<string, PropertyMapper> GetProperties(Type type,
            Func<string, string> resolvePropertyName)
        {
            var dict = new Dictionary<string, PropertyMapper>(StringComparer.OrdinalIgnoreCase);
            var id = GetIdProperty(type);
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

            foreach (var prop in props)
            {
                // ignore indexer property
                if (prop.GetIndexParameters().Length > 0) continue;

                // ignore not read/write
                ////if (!prop.CanRead || !prop.CanWrite) continue;
                if (!prop.CanRead) continue;

                var mapper =
                    (LiteMapperAttribute) prop.GetCustomAttributes(typeof (LiteMapperAttribute), false).FirstOrDefault() ??
                    new LiteMapperAttribute();

                // [BsonIgnore]
                if (mapper.Ignore) continue;

                // check if property has [BsonField]
                var bsonField = prop.IsDefined(typeof (LiteMapperAttribute), false);

                // create getter/setter IL function
                var getter = CreateGetMethod(type, prop, bsonField);
                var setter = CreateSetMethod(type, prop, bsonField);

                // if not getter or setter - no mapping
                if (getter == null) continue;

                var name = id != null && id.Equals(prop) ? "_id" : resolvePropertyName(prop.Name);

                // check if property has [BsonField] with a custom field name
                if (!string.IsNullOrEmpty(mapper.FieldName))
                {
                    name = mapper.FieldName;
                }

                // test if field name is OK (avoid to check in all instances) - do not test internal classes, like DbRef
                if (BsonDocument.IsValidFieldName(name) == false) throw LiteException.InvalidFormat(prop.Name, name);

                // create a property mapper
                var p = new PropertyMapper
                {
                    AutoId = mapper.AutoID != AutoID.False,
                    FieldName = name,
                    PropertyName = prop.Name,
                    PropertyType = prop.PropertyType,
                    IndexOptions = name == "_id" || !bsonField ? null : mapper.Indexes,
                    Getter = getter,
                    Setter = setter
                };

                dict.Add(prop.Name, p);
            }

            return dict;
        }

        #endregion GetProperties

        private delegate object CreateObject();

        #region GetIdProperty

        /// <summary>
        ///     Gets PropertyInfo that refers to Id from a document object.
        /// </summary>
        public static PropertyInfo GetIdProperty(Type type)
        {
            // Get all properties and test in order: BsonIdAttribute, "Id" name, "<typeName>Id" name
            return
                SelectProperty(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic),
                    x =>
                        ((LiteMapperAttribute) Attribute.GetCustomAttribute(x, typeof (LiteMapperAttribute), true) ??
                         new LiteMapperAttribute()).AutoID == AutoID.True,
                    x => x.Name.Equals("Id", StringComparison.OrdinalIgnoreCase),
                    x => x.Name.Equals(type.Name + "Id", StringComparison.OrdinalIgnoreCase));
        }

        private static PropertyInfo SelectProperty(IEnumerable<PropertyInfo> props,
            params Func<PropertyInfo, bool>[] predicates)
        {
            foreach (var predicate in predicates)
            {
                var prop = props.FirstOrDefault(predicate);

                if (prop != null)
                {
                    if (!prop.CanRead || !prop.CanWrite)
                    {
                        throw LiteException.PropertyReadWrite(prop);
                    }

                    return prop;
                }
            }

            return null;
        }

        #endregion GetIdProperty

        #region IL Code

        /// <summary>
        ///     Create a new instance from a Type
        /// </summary>
        public static object CreateInstance(Type type)
        {
            try
            {
                CreateObject c;
                if (_cacheCtor.TryGetValue(type, out c))
                {
                    return c();
                }
            }
            catch
            {
                throw LiteException.InvalidCtor(type);
            }

            lock (_cacheCtor)
            {
                try
                {
                    CreateObject c = null;

                    if (_cacheCtor.TryGetValue(type, out c))
                    {
                        return c();
                    }
                    if (type.IsClass)
                    {
                        var dynMethod = new DynamicMethod("_", type, null);
                        var il = dynMethod.GetILGenerator();
                        il.Emit(OpCodes.Newobj, type.GetConstructor(Type.EmptyTypes));
                        il.Emit(OpCodes.Ret);
                        c = (CreateObject) dynMethod.CreateDelegate(typeof (CreateObject));
                        _cacheCtor.Add(type, c);
                    }
                    else if (type.IsInterface) // some know interfaces
                    {
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (IList<>))
                        {
                            return CreateInstance(GetGenericListOfType(UnderlyingTypeOf(type)));
                        }
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (ICollection<>))
                        {
                            return CreateInstance(GetGenericListOfType(UnderlyingTypeOf(type)));
                        }
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (IEnumerable<>))
                        {
                            return CreateInstance(GetGenericListOfType(UnderlyingTypeOf(type)));
                        }
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (IDictionary<,>))
                        {
                            var k = type.GetGenericArguments()[0];
                            var v = type.GetGenericArguments()[1];
                            return CreateInstance(GetGenericDictionaryOfType(k, v));
                        }
                        throw LiteException.InvalidCtor(type);
                    }
                    else // structs
                    {
                        var dynMethod = new DynamicMethod("_", typeof (object), null);
                        var il = dynMethod.GetILGenerator();
                        var lv = il.DeclareLocal(type);
                        il.Emit(OpCodes.Ldloca_S, lv);
                        il.Emit(OpCodes.Initobj, type);
                        il.Emit(OpCodes.Ldloc_0);
                        il.Emit(OpCodes.Box, type);
                        il.Emit(OpCodes.Ret);
                        c = (CreateObject) dynMethod.CreateDelegate(typeof (CreateObject));
                        _cacheCtor.Add(type, c);
                    }

                    return c();
                }
                catch (Exception)
                {
                    throw LiteException.InvalidCtor(type);
                }
            }
        }

        private static GenericGetter CreateGetMethod(Type type, PropertyInfo propertyInfo, bool nonPublic)
        {
            //nonPublic: Indicates whether a non-public get accessor should be returned.
            var getMethod = propertyInfo.GetGetMethod(nonPublic);
            if (getMethod == null) return null;

            var getter = new DynamicMethod("_", typeof (object), new[] {typeof (object)}, type, true);
            var il = getter.GetILGenerator();

            if (!type.IsClass) // structs
            {
                var lv = il.DeclareLocal(type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox_Any, type);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, lv);
                il.EmitCall(OpCodes.Call, getMethod, null);
                if (propertyInfo.PropertyType.IsValueType)
                    il.Emit(OpCodes.Box, propertyInfo.PropertyType);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                il.EmitCall(OpCodes.Callvirt, getMethod, null);
                if (propertyInfo.PropertyType.IsValueType)
                    il.Emit(OpCodes.Box, propertyInfo.PropertyType);
            }

            il.Emit(OpCodes.Ret);

            return (GenericGetter) getter.CreateDelegate(typeof (GenericGetter));
        }

        private static GenericSetter CreateSetMethod(Type type, PropertyInfo propertyInfo, bool nonPublic)
        {
            //nonPublic: Indicates whether a non-public set accessor should be returned.
            var setMethod = propertyInfo.GetSetMethod(nonPublic);

            if (setMethod == null) return null;

            var setter = new DynamicMethod("_", typeof (object), new[] {typeof (object), typeof (object)}, true);
            var il = setter.GetILGenerator();

            if (!type.IsClass) // structs
            {
                var lv = il.DeclareLocal(type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox_Any, type);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, lv);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(propertyInfo.PropertyType.IsClass ? OpCodes.Castclass : OpCodes.Unbox_Any,
                    propertyInfo.PropertyType);
                il.EmitCall(OpCodes.Call, setMethod, null);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Box, type);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(propertyInfo.PropertyType.IsClass ? OpCodes.Castclass : OpCodes.Unbox_Any,
                    propertyInfo.PropertyType);
                il.EmitCall(OpCodes.Callvirt, setMethod, null);
                il.Emit(OpCodes.Ldarg_0);
            }

            il.Emit(OpCodes.Ret);

            return (GenericSetter) setter.CreateDelegate(typeof (GenericSetter));
        }

        #endregion IL Code

        #region Utils

        public static bool IsNullable(Type type)
        {
            if (!type.IsGenericType) return false;
            var g = type.GetGenericTypeDefinition();
            return g.Equals(typeof (Nullable<>));
        }

        public static Type UnderlyingTypeOf(Type type)
        {
            return type.GetGenericArguments()[0];
        }

        public static Type GetGenericListOfType(Type type)
        {
            var listType = typeof (List<>);
            return listType.MakeGenericType(type);
        }

        public static Type GetGenericDictionaryOfType(Type k, Type v)
        {
            var listType = typeof (Dictionary<,>);
            return listType.MakeGenericType(k, v);
        }

        public static Type GetListItemType(object list)
        {
            var type = list.GetType();

            if (type.IsArray) return type.GetElementType();

            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof (IEnumerable<>))
                {
                    return i.GetGenericArguments()[0];
                }
            }

            return typeof (object);
        }

        /// <summary>
        ///     Returns true if Type is any kind of Array/IList/ICollection/....
        /// </summary>
        public static bool IsList(Type type)
        {
            if (type.IsArray) return true;

            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.IsGenericType)
                {
                    if (@interface.GetGenericTypeDefinition() == typeof (IEnumerable<>))
                    {
                        // if needed, you can also return the type used as generic argument
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion Utils
    }
}