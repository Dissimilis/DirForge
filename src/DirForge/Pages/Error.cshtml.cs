using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace DirForge.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    private readonly ILogger<ErrorModel> _logger;

    public ErrorModel(ILogger<ErrorModel> logger)
    {
        _logger = logger;
    }

    public new int StatusCode { get; set; } = 500;
    public string StatusMessage { get; set; } = "Something went wrong.";

    public void OnGet(int? statusCode = null)
    {
        var exceptionHandlerPathFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (exceptionHandlerPathFeature?.Error != null)
        {
            _logger.LogError(exceptionHandlerPathFeature.Error, "Unhandled exception captured by Error page.");
        }

        if (statusCode.HasValue)
        {
            StatusCode = statusCode.Value;
            StatusMessage = ReasonPhrases.GetReasonPhrase(StatusCode) ?? "An error occurred.";
        }
        else
        {
            var statusCodeReExecuteFeature = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
            if (statusCodeReExecuteFeature != null)
            {
                // We're here because of an unhandled exception or explicit 500
                StatusCode = 500;
                StatusMessage = "Something went wrong.";
            }
        }

        HttpContext.Response.StatusCode = StatusCode;
    }
}
