using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;

namespace SolarPortal.Web.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITempDataDictionaryFactory tempDataFactory)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);

            // AJAX → JSON response
            if (context.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "An unexpected error occurred. Please try again."
                }));
                return;
            }

            // Regular page request → put friendly message into TempData (toast)
            // and bounce back to Dashboard (if logged in) or Login. The user
            // never sees the scary red "Error" page.
            var temp = tempDataFactory.GetTempData(context);
            if (!temp.ContainsKey("Error") && !temp.ContainsKey("Warning"))
            {
                temp["Error"] = "Something went wrong while processing your request. Please try again.";
                temp.Save();
            }

            var redirectTo = context.User?.Identity?.IsAuthenticated == true
                ? "/SolarPanelUserPanel/Dashboard"
                : "/Account/Login";
            context.Response.Redirect(redirectTo);
        }
    }
}
