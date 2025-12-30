using Microsoft.Playwright;

internal sealed record DownloadCandidate(ILocator Locator, string Href, string PlaceholderName);
