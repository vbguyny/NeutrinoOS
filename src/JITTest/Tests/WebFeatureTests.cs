// JITTest - Web feature collection tests (Kestrel port M3).
//
// IFeatureCollection lookups perform Type-token comparison plus a guarded
// object->TFeature cast - a shape the Tier-0 JIT has to get right before
// anything in the web layer depends on it (the generic-cast family has
// burned us before; see the ReadOnlySequence notes in korlib). These tests
// pin the round-trip down on-device.

using NeutrinoOS.DDK.Web;

namespace JITTest;

public static class WebFeatureTests
{
    public static void RunAll()
    {
        var request = new HttpRequest();
        var response = new HttpResponse();
        var features = new HttpFeatureCollection(request, response);

        IHttpRequestFeature req = features.Get<IHttpRequestFeature>();
        TestTracker.Record("Features/get-request", req != null);
        if (req != null)
        {
            req.Method = "PUT";
            TestTracker.Record("Features/request-writes-through", request.Method == "PUT");
        }

        IHttpResponseFeature resp = features.Get<IHttpResponseFeature>();
        TestTracker.Record("Features/get-response", resp != null);
        if (resp != null)
        {
            resp.StatusCode = 201;
            resp.Write("x");
            TestTracker.Record("Features/response-writes-through",
                response.StatusCode == 201 && response.Body == "x");
        }

        features.Set<IHttpResponseFeature>(null);
        TestTracker.Record("Features/clear", features.Get<IHttpResponseFeature>() == null);
        TestTracker.Record("Features/request-untouched", features.Get<IHttpRequestFeature>() != null);
    }
}
