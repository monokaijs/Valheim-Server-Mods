using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace XomNghien.Bootstrap;

internal static class RpcReflectionBridge
{
    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void RegisterString(object rpc, string name, Action<object, string> handler)
    {
        var openRegister = rpc.GetType().GetMethods(AllMembers)
            .Where(method => method.Name == "Register" && method.IsGenericMethodDefinition)
            .Where(method => method.GetGenericArguments().Length == 1)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 2 && parameters[0].ParameterType == typeof(string);
            }) ?? throw new MissingMethodException(rpc.GetType().FullName, "Register<T>(string, callback)");
        var register = openRegister.MakeGenericMethod(typeof(string));
        var callbackType = register.GetParameters()[1].ParameterType;
        var invoke = callbackType.GetMethod("Invoke") ?? throw new InvalidOperationException("RPC callback type is not a delegate");
        var callbackParameters = invoke.GetParameters();
        if (callbackParameters.Length != 2 || callbackParameters[1].ParameterType != typeof(string))
            throw new InvalidOperationException("Valheim string RPC callback signature is unsupported");

        var rpcParameter = Expression.Parameter(callbackParameters[0].ParameterType, "rpc");
        var payloadParameter = Expression.Parameter(typeof(string), "payload");
        var body = Expression.Invoke(
            Expression.Constant(handler),
            Expression.Convert(rpcParameter, typeof(object)),
            payloadParameter);
        var callback = Expression.Lambda(callbackType, body, rpcParameter, payloadParameter).Compile();
        register.Invoke(rpc, new object[] { name, callback });
    }

    public static void InvokeString(object rpc, string name, string payload)
    {
        var invoke = rpc.GetType().GetMethods(AllMembers)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Invoke") return false;
                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(object[]);
            }) ?? throw new MissingMethodException(rpc.GetType().FullName, "Invoke(string, object[])");
        invoke.Invoke(rpc, new object[] { name, new object[] { payload } });
    }
}
