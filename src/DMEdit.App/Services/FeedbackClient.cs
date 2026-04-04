using System.Net.Http;
using System.Text;

namespace DMEdit.App.Services;

/// <summary>
/// Submits feedback payloads to the Azure Function endpoint.
/// Falls back to a pre-filled GitHub issue URL if the endpoint is unreachable.
/// </summary>
public static class FeedbackClient {
    private const string EndpointUrl =
        "https://devmentalsubmitissue-bxgsbyhwhteabcaj.centralus-01.azurewebsites.net/api/SubmitIssue?code=8Ctv7Q4dqGhP-fe0kGaV841oDZ6C3zWzqIHYHNv4S6otAzFuRflUCg==";

    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Posts the payload to the Azure Function. Returns null on success,
    /// or an error message string on failure.
    /// </summary>
    public static async Task<string?> SubmitAsync(FeedbackPayload payload) {
        try {
            var json = payload.ToJson();
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(EndpointUrl, content);

            if (response.IsSuccessStatusCode) {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            return $"Server returned {(int)response.StatusCode}: {body}";
        } catch (TaskCanceledException) {
            return "Request timed out. Check your internet connection.";
        } catch (HttpRequestException ex) {
            return $"Network error: {ex.Message}";
        } catch (Exception ex) {
            return $"Unexpected error: {ex.Message}";
        }
    }
}
