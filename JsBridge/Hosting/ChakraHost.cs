﻿using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using JSBridge.Hosting;
using JSBridge.Hosting.Functions;

namespace JSBridge
{
    public class ChakraHost
    {
        private static JavaScriptSourceContext currentSourceContext = JavaScriptSourceContext.FromIntPtr(IntPtr.Zero);
        private static JavaScriptRuntime runtime;
        private static JavaScriptValue promiseCallback;

        public async Task<string> InitAsync()
        {
            JavaScriptContext context;

            if (Native.JsCreateRuntime(JavaScriptRuntimeAttributes.None, null, out runtime) !=
                JavaScriptErrorCode.NoError)
                return "failed to create runtime.";

            if (Native.JsCreateContext(runtime, out context) != JavaScriptErrorCode.NoError)
                return "failed to create execution context.";

            if (Native.JsSetCurrentContext(context) != JavaScriptErrorCode.NoError)
                return "failed to set current context.";

            // ES6 Promise callback
            JavaScriptPromiseContinuationCallback promiseContinuationCallback =
                delegate(JavaScriptValue task, IntPtr callbackState)
                {
                    promiseCallback = task;
                };

            if (Native.JsSetPromiseContinuationCallback(promiseContinuationCallback, IntPtr.Zero) !=
                JavaScriptErrorCode.NoError)
                return "failed to setup callback for ES6 Promise";

            // Bind to global object
            // setTimeout
            DefineHostCallback("setTimeout", SetTimeout.SetTimeoutJavaScriptNativeFunction);

            // Projections
            if (Native.JsProjectWinRTNamespace("JSE") != JavaScriptErrorCode.NoError)
                return "failed to project JSE namespace.";

            if (Native.JsProjectWinRTNamespace("Models") != JavaScriptErrorCode.NoError)
                return "failed to project Models namespace.";

            if (Native.JsProjectWinRTNamespace("ViewModels") != JavaScriptErrorCode.NoError)
                return "failed to project ViewModels namespace.";

            ProjectObjectToGlobal(new JSE.console(), "console");
            ProjectObjectToGlobal(new JSE.window(), "window");
            ProjectObjectToGlobal(new ViewModels.peopleManager(), "peopleManager");

            // Add references
            await AddScriptReferenceAsync("injection.js");
            await AddScriptReferenceAsync("cdc.js");
            await AddScriptReferenceAsync("azuremobileservices.js");
            await AddScriptReferenceAsync("cdc-azuremobileservices.js");

            // Debug
            if (Native.JsStartDebugging() != JavaScriptErrorCode.NoError)
                return "failed to start debugging.";

            return "NoError";
        }

        public async Task<string> AddScriptReferenceAsync(string name)
        {
            var scriptFile = await CoreTools.GetPackagedFileAsync("refs", name);
            var script = await FileIO.ReadTextAsync(scriptFile);

            return RunScript(script);
        }

        public async Task<string> AddScriptHttpReferenceAsync(string url)
        {
            var script = await CoreTools.DownloadStringAsync(url);

            return RunScript(script);
        }

        public string RunScript(string script)
        {
            IntPtr returnValue;

            try
            {
                JavaScriptValue result;

                if (Native.JsRunScript(script, currentSourceContext++, "", out result) != JavaScriptErrorCode.NoError)
                {
                    // Get error message and clear exception
                    JavaScriptValue exception;
                    if (Native.JsGetAndClearException(out exception) != JavaScriptErrorCode.NoError)
                        return "failed to get and clear exception";

                    JavaScriptPropertyId messageName;
                    if (Native.JsGetPropertyIdFromName("message",
                        out messageName) != JavaScriptErrorCode.NoError)
                        return "failed to get error message id";

                    JavaScriptValue messageValue;
                    if (Native.JsGetProperty(exception, messageName, out messageValue)
                        != JavaScriptErrorCode.NoError)
                        return "failed to get error message";

                    IntPtr message;
                    UIntPtr length;
                    if (Native.JsStringToPointer(messageValue, out message, out length) != JavaScriptErrorCode.NoError)
                        return "failed to convert error message";

                    return Marshal.PtrToStringUni(message);
                }

                // Execute promise tasks stored in promiseCallback 
                while (promiseCallback.IsValid)
                {
                    JavaScriptValue task = promiseCallback;
                    promiseCallback = JavaScriptValue.Invalid;
                    JavaScriptValue promiseResult;
                    Native.JsCallFunction(task, null, 0, out promiseResult);
                }

                // Convert the return value.
                JavaScriptValue stringResult;
                UIntPtr stringLength;
                if (Native.JsConvertValueToString(result, out stringResult) != JavaScriptErrorCode.NoError)
                    return "failed to convert value to string.";
                if (Native.JsStringToPointer(stringResult, out returnValue, out stringLength) !=
                    JavaScriptErrorCode.NoError)
                    return "failed to convert return value.";
            }
            catch (Exception e)
            {
                return "chakrahost: fatal error: internal error: " + e.Message;
            }

            return Marshal.PtrToStringUni(returnValue);
        }

        private static void DefineHostCallback(string callbackName, JavaScriptNativeFunction callback)
        {
            JavaScriptValue globalObject;
            Native.JsGetGlobalObject(out globalObject);

            JavaScriptPropertyId propertyId = JavaScriptPropertyId.FromString(callbackName);
            JavaScriptValue function = JavaScriptValue.CreateFunction(callback, IntPtr.Zero);

            globalObject.SetProperty(propertyId, function, true);
        }

        private static void DefineHostProperty(string callbackName, JavaScriptValue value)
        {
            JavaScriptValue globalObject;
            Native.JsGetGlobalObject(out globalObject);

            JavaScriptPropertyId propertyId = JavaScriptPropertyId.FromString(callbackName);
            globalObject.SetProperty(propertyId, value, true);
        }

        private static string ProjectObjectToGlobal(object objectToProject, string name)
        {
            JavaScriptValue value;
            if (Native.JsInspectableToObject(objectToProject, out value) != JavaScriptErrorCode.NoError)
                return $"failed to project {name} object";

            DefineHostProperty(name, value);

            return "NoError";
        }
    }
}