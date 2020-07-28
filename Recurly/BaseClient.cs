using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;
using RestSharp.Authenticators;

[assembly: InternalsVisibleTo("Recurly.Tests")]

namespace Recurly
{
    public class BaseClient
    {
        private string ApiKey { get; }
        private const string ApiUrl = "https://v3.recurly.com/";
        private string[] BinaryTypes = { "application/pdf" };
        public virtual string ApiVersion { get; protected set; }

        internal IRestClient RestClient { get; set; }

        public BaseClient(string apiKey)
        {
            if (String.IsNullOrEmpty(apiKey))
                throw new ArgumentException($"apiKey is required. You passed in {apiKey}");

            ApiKey = apiKey;
            RestClient = new RestClient();
            RestClient.BaseUrl = new Uri(ApiUrl);
            RestClient.Authenticator = new HttpBasicAuthenticator(ApiKey, "");

            // AddDefaultHeader does not work for user-agent
            var libVersion = typeof(Recurly.Client).Assembly.GetName().Version;
            RestClient.UserAgent = $"Recurly/{libVersion}; .NET";

            Array.ForEach(BinaryTypes, contentType =>
                RestClient.AddHandler(contentType, () => { return new Recurly.FileSerializer(); })
            );
            RestClient.AddHandler("application/json", () => { return new JsonSerializer(); });


            // These are the default headers to send on every request
            RestClient.AddDefaultHeader("Accept", $"application/vnd.recurly.{ApiVersion}");
            RestClient.AddDefaultHeader("Content-Type", "application/json");
        }

        public async Task<T> MakeRequestAsync<T>(Method method, string url, Request body = null, Dictionary<string, object> queryParams = null, RequestOptions options = null, CancellationToken cancellationToken = default(CancellationToken)) where T : Resource
        {
            Debug.WriteLine($"Calling {url}");
            var request = BuildRequest(method, url, body, queryParams, options);
            var task = RestClient.ExecuteAsync<T>(request, cancellationToken);
            return await task.ContinueWith(t =>
            {
                var resp = t.Result;
                this.HandleResponse(resp);
                if (resp.Data is Resource)
                    resp.Data.SetResponse(Response.Build(resp));
                return resp.Data;
            });
        }

        public T MakeRequest<T>(Method method, string url, Request body = null, Dictionary<string, object> queryParams = null, RequestOptions options = null) where T : Resource
        {
            Debug.WriteLine($"Calling {url}");
            var request = BuildRequest(method, url, body, queryParams, options);
            var resp = RestClient.Execute<T>(request);
            this.HandleResponse(resp);
            if (resp.Data is Resource)
                resp.Data.SetResponse(Response.Build(resp));
            return resp.Data;
        }

        public int GetResourceCount(string url, Dictionary<string, object> queryParams)
        {
            Debug.WriteLine($"Calling {url}");
            var request = BuildRequest(Method.HEAD, url, null, queryParams);
            var resp = RestClient.Execute(request);
            this.HandleResponse(resp);
            var headers = resp.Headers.ToList();
            var recordCount = headers
                .Find(x => x.Name == "Recurly-Total-Records")
                .Value.ToString();
            return int.Parse(recordCount);
        }

        [ExcludeFromCodeCoverage]
        public void _SetApiUrl(string uri)
        {
            Console.WriteLine("[SECURITY WARNING] _SetApiUrl is for testing only and not supported in production.");
            if (System.Environment.GetEnvironmentVariable("RECURLY_INSECURE") == "true")
            {
                this.RestClient.BaseUrl = new Uri(uri);
            }
            else
            {
                Console.WriteLine("ApiUrl not changed. To change, set the environment variable RECURLY_INSECURE to true");
            }
        }

        private RestRequest BuildRequest(Method method, string url, Request body = null, Dictionary<string, object> queryParams = null, RequestOptions options = null)
        {
            if (options == null)
            {
                options = new RequestOptions();
            }
            // If we have any query params, add them to the request
            if (queryParams != null)
            {
                url += Utils.QueryString(queryParams);
            }

            var request = new RestRequest(url, method);
            request.JsonSerializer = Recurly.JsonSerializer.Default;
            request.AddHeaders(options.Headers);

            // If we have a body, serialize it and add it to the request
            if (body != null)
            {
                request.AddJsonBody(body);
            }

            return request;
        }

        private void HandleResponse(IRestResponse resp)
        {
            if (resp.Headers.Any(t => t.Name == "Recurly-Deprecated"))
            {
                var headers = resp.Headers.ToList();
                var deprecated = headers
                    .Find(x => x.Name == "Recurly-Deprecated")
                    .Value.ToString();
                var sunset = headers
                    .Find(x => x.Name == "Recurly-Sunset-Date")
                    .Value.ToString();

                if (deprecated.ToUpper() == "TRUE")
                {
                    Debug.WriteLine($"[recurly-client-net] WARNING: Your current API version \"${ApiVersion}\" is deprecated and will be sunset on ${sunset}");
                }
            }

            var status = (int)resp.StatusCode;
            Debug.WriteLine($"Status: {status}");
            Debug.WriteLine($"Content: {resp.Content}");

            // If the response has an ErrorException,
            // an error casting the json to a Resource
            // has likely occurred
            if (resp.ErrorException != null)
            {
                throw new RecurlyError(resp.ErrorMessage);
            }
            else if (status < 200 || status >= 300)
            {
                // Turn web exceptions into Recurly.NetworkErrors
                if (resp.ErrorException is WebException)
                {
                    var netError = new Errors.NetworkError(resp.ErrorMessage);
                    netError.ExceptionStatus = ((WebException)resp.ErrorException).Status;
                    throw netError;
                }
                // everything else becomes a Recurly.ApiError
                else
                {
                    var serializer = Recurly.JsonSerializer.Default;
                    var err = serializer.Deserialize<Errors.ApiErrorWrapper>(resp).Error;
                    var ex = Errors.Factory.Create(err);
                    throw ex;
                }
            }
        }

        private void ValidatePathParameters(Dictionary<string, object> urlParams)
        {
            var invalidParams = urlParams.Where(kvp => string.IsNullOrWhiteSpace(kvp.Value.ToString()));
            if (invalidParams.Any())
            {
                var invalidKeys = string.Join(", ", invalidParams.Select(x => x.Key).ToArray());
                throw new RecurlyError($"{invalidKeys} cannot be an empty value");
            }

        }

        protected string InterpolatePath(string path, Dictionary<string, object> urlParams)
        {
            ValidatePathParameters(urlParams);
            var regex = new Regex("{([A-Za-z|_]*)}");
            // TODO ToString() here might not appropriately format all data types
            // such as datetimes
            // Encode forward slashes in the url components. Standard encoding will happen within
            // the RestSharp library.
            return regex.Replace(path, m => urlParams[m.Groups[1].Value].ToString().Replace("/", "%2F"));
        }
    }
}
