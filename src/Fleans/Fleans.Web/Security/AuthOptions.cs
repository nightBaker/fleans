namespace Fleans.Web.Security;

// Singleton record built once at startup from configuration. Razor components inject it
// to decide whether to render auth-aware UI (cascading auth state, login redirect).
// Auth is enabled iff both Authority and ClientId are non-empty — the same single
// source of truth used by Program.cs to gate middleware registration.
public sealed record AuthOptions(bool Enabled, string Authority, string ClientId);
