// The Bridge wire implementation (JSON, framing, pipe server core) is internal: it is compiled into
// Contracts for tests and linked as source into the Bridge add-in. Only the test project sees it here.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RevitAgent.Tests")]
