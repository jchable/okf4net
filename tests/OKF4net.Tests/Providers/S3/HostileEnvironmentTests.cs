// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec D2, D3, T1 "Environnement hostile", Annexe B G1/G2: nothing ambient — AWS_* variables, an AWS config file and
/// profile, proxy variables, CSM — changes what the provider sends, and no CSM datagram leaves. Each hostile
/// environment runs twice in a child process (tests/S3HostileEnvProbe): once through the provider's public constructor,
/// whose lines must equal the clean run's, and once through plain AmazonS3Clients, the POSITIVE WITNESS, which must
/// show — effect by effect, each asserted on its own — that the environment really is hostile to an unguarded client.
/// </summary>
/// <remarks>
/// Every witness below was measured on the real SDK (AWSSDK.S3 4.1.0 / AWSSDK.Core 4.1.0, .NET 10, Windows 11,
/// 2026-11-18; the values in the comments are those measurements). The ambient settings no plain client shows in this
/// harness are named in the comment of the case that sets them, with what was measured; the provider's run is held to
/// equality under them all the same. Several hostile environments are needed because some effects mask the others on a
/// plain client: FIPS or dual-stack with a custom endpoint, or an unparsable defaults mode, fail every request before
/// it is sent.
/// What this test does NOT pin by itself (mutation runs, 2026-11-18): removing IgnoreConfiguredEndpointUrls leaves it
/// green, because the provider here is configured with an explicit ServiceUrl, which wins over every ambient endpoint
/// setting (the plain witness measured the same). A Region source here would send its clean run off the machine, so
/// that row is pinned in-process instead, over the scripted fake, by <see cref="AmbientEndpointTests"/>. ClientAppId = "" and the guard's User-Agent allowlist (which drops any app/ token) are two layers: removing either alone leaves the
/// app/ witness green here (GuardTests pins the guard's), removing both turns it red.
/// Removing DefaultConfigurationMode = Standard also leaves it green (mutation run, 2026-11-19): measured, the provider's
/// requests are the same without it under AWS_DEFAULTS_MODE auto, mobile or in-region, and it contacts no
/// instance-metadata endpoint, since every value a defaults mode would choose is set explicitly; the two defaults-mode
/// cases keep that so on every SDK bump. Sweep of every AWS_* variable and profile key in AWSSDK.Core/S3 4.1.0, its
/// classification and the two header contents the guard now removes (x-amzn-trace-id in Lambda, dropped; AWS_INTERNAL_ENV's
/// text, dropped by the User-Agent allowlist; case variables-lambda-internal-env): spec Annexe C12.
/// </remarks>
public sealed class HostileEnvironmentTests(HostileEnvironmentTests.Baseline baseline) : IClassFixture<HostileEnvironmentTests.Baseline>
{
    private const string HostileProfile = "hostile";

    /// <summary>The ports, the clean environment and the two clean runs, shared by every case (one port set for all).</summary>
    public sealed class Baseline : IAsyncLifetime
    {
        public TempDir Home { get; } = new();

        public int S3 { get; private set; }

        public int Evil { get; private set; }

        public int Proxy { get; private set; }

        public int Udp { get; private set; }

        public string[] CleanProvider { get; private set; } = [];

        public string[] CleanPlain { get; private set; } = [];

        public async Task InitializeAsync()
        {
            // Four distinct ports, held together while chosen so the OS cannot hand the same one out twice.
            var tcp = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            try
            {
                foreach (var t in tcp)
                {
                    t.Start();
                }

                (S3, Evil, Proxy) = (Port(tcp[0]), Port(tcp[1]), Port(tcp[2]));
                Udp = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            }
            finally
            {
                foreach (var t in tcp)
                {
                    t.Stop();
                }
            }

            udp.Close();
            File.WriteAllText(Path.Combine(Home.Path, "empty"), "");
            CleanProvider = await RunAsync(Clean(), "provider");
            CleanPlain = await RunAsync(Clean(), "plain");
        }

        public Task DisposeAsync()
        {
            Home.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// The inherited environment without any AWS_*, _X_AMZN_TRACE_ID or proxy variable (NO_PROXY included, any case), with an empty
        /// config and credentials file instead of the machine's ~/.aws, and the instance-metadata service off.
        /// </summary>
        public Dictionary<string, string?> Clean()
        {
            var env = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("_X_AMZN_TRACE_ID", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(k => k, _ => (string?)null);
            var empty = Path.Combine(Home.Path, "empty");
            env["AWS_CONFIG_FILE"] = empty;
            env["AWS_SHARED_CREDENTIALS_FILE"] = empty;
            env["AWS_EC2_METADATA_DISABLED"] = "true";
            return env;
        }

        public async Task<string[]> RunAsync(Dictionary<string, string?> env, string mode)
        {
            var args = new[] { TestProcess.S3HostileEnvProbePath(), Text(S3), Text(Evil), Text(Proxy), Text(Udp), mode };
            var (code, output, error) = await TestProcess.RunAsync(TestProcess.DotnetHost(), args, env, TimeSpan.FromSeconds(90));
            Assert.True(code == 0, $"probe exited {code}: {error}");
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static int Port(TcpListener t) => ((IPEndPoint)t.LocalEndpoint).Port;
    }

    public static TheoryData<string> Cases() =>
        ["variables", "variables-fips-dualstack", "variables-us-east-1-legacy", "variables-defaults-mode", "profile", "profile-fips-dualstack", "profile-defaults-mode", "variables-lambda-internal-env", "variables-imds"];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hostile_environment_changes_nothing_the_provider_sends_and_a_plain_client_proves_it_is_hostile(string hostileCase)
    {
        var env = Hostile(hostileCase);
        var plain = await baseline.RunAsync(env, "plain");
        WithOutput("plain witness", plain, () => AssertWitnesses(hostileCase, plain));

        var provider = await baseline.RunAsync(env, "provider");
        WithOutput("provider", provider, () =>
        {
            // Request lines, every header but date and invocation id, signed headers, scope, UDP count; joined, so a
            // difference is reported in full rather than in truncated collection items.
            Assert.Equal(string.Join("\n", baseline.CleanProvider), string.Join("\n", provider));
            Assert.Contains("concepts=1", provider);
            Assert.Contains("UDP 0", provider);
            Assert.Equal(3, provider.Count(l => l.StartsWith("s3 | GET /okf/", StringComparison.Ordinal))); // two listings (load, survey) and one read
            Assert.DoesNotContain(provider, l => l.StartsWith("evil |", StringComparison.Ordinal));
            Assert.DoesNotContain(provider, l => l.StartsWith("proxy |", StringComparison.Ordinal));
            Assert.DoesNotContain(provider, l => l.Contains("MARK", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void The_clean_runs_are_what_the_witnesses_compare_against()
    {
        WithOutput("clean provider", baseline.CleanProvider, () =>
        {
            Assert.Contains("concepts=1", baseline.CleanProvider);
            Assert.Contains("UDP 0", baseline.CleanProvider);
        });
        WithOutput("clean plain witness", baseline.CleanPlain, () =>
        {
            Assert.Contains("UDP 0", baseline.CleanPlain);
            Assert.Contains("plain ua=ok", baseline.CleanPlain);
            Assert.Contains("plain endpoint refused-hop=s3.us-east-1.amazonaws.com", baseline.CleanPlain);
            Assert.Contains("plain proxy refused-hop=okf-witness.invalid", baseline.CleanPlain);
            var ua = Single(baseline.CleanPlain, "ua");
            Assert.StartsWith("s3 | GET /okf/kb/ua.md HTTP/1.1 |", ua, StringComparison.Ordinal);
            Assert.Contains("amz-sdk-request=attempt=1; max=3", ua, StringComparison.Ordinal);
            Assert.Contains("x-amz-checksum-mode=ENABLED", ua, StringComparison.Ordinal);
            Assert.DoesNotContain(" app/", ua, StringComparison.Ordinal);
            Assert.DoesNotContain(" exec-env/", ua, StringComparison.Ordinal);
            Assert.DoesNotContain("x-amzn-trace-id", ua, StringComparison.Ordinal);
            Assert.DoesNotContain("IENVMARK", ua, StringComparison.Ordinal);
            Assert.Contains("E", Metrics(ua)); // the standard retry mode's business metric
            Assert.DoesNotContain("F", Metrics(ua)); // the adaptive one's
            Assert.Contains("P", Metrics(ua)); // the preferred account-id endpoint mode's
            Assert.DoesNotContain("R", Metrics(ua)); // the required one's
        });
    }

    /// <summary>Runs <paramref name="check"/>; a failure is reported with the probe's raw output, so it shows what was sent.</summary>
    private static void WithOutput(string run, string[] output, Action check)
    {
        try
        {
            check();
        }
        catch (Xunit.Sdk.XunitException e)
        {
            throw new Xunit.Sdk.XunitException($"{e.Message}\n--- {run} probe output ---\n{string.Join("\n", output)}");
        }
    }

    private Dictionary<string, string?> Hostile(string hostileCase)
    {
        var env = baseline.Clean();
        var evil = $"http://127.0.0.1:{Text(baseline.Evil)}";
        var proxy = $"http://127.0.0.1:{Text(baseline.Proxy)}";
        var udp = Text(baseline.Udp);
        switch (hostileCase)
        {
            case "variables":
                // Witnessed on a plain client (AssertWitnesses): the endpoint, proxy, retry, User-Agent, checksum and CSM
                // variables. Set but NOT observable on a plain client in this harness (measured): AWS_REQUEST_CHECKSUM_CALCULATION
                // (a GET has no body to checksum), AWS_AUTH_SCHEME_PREFERENCE=sigv4a (still AWS4-HMAC-SHA256),
                // AWS_REGION/AWS_DEFAULT_REGION (every client names its region), the credential variables (every client
                // is given explicit credentials), AWS_DISABLE_CLOCK_SKEW_CORRECTION (no skew), AWS_ACCOUNT_ID, the
                // multi-region access point and express-session switches (no such bucket), AWS_IGNORE_CONFIGURED_ENDPOINT_URLS=false
                // (the default) and AWS_S3_US_EAST_1_REGIONAL_ENDPOINT (masked here by AWS_ENDPOINT_URL_S3: its own case
                // below).
                Set(env, new()
                {
                    ["AWS_ENDPOINT_URL_S3"] = evil,
                    ["AWS_ENDPOINT_URL"] = evil,
                    ["AWS_IGNORE_CONFIGURED_ENDPOINT_URLS"] = "false",
                    ["AWS_RETRY_MODE"] = "adaptive",
                    ["AWS_MAX_ATTEMPTS"] = "9",
                    ["AWS_SDK_UA_APP_ID"] = "ENVMARK",
                    ["AWS_EXECUTION_ENV"] = "EXECMARK",
                    ["AWS_CSM_ENABLED"] = "true",
                    ["AWS_CSM_PORT"] = udp,
                    ["AWS_CSM_HOST"] = "127.0.0.1",
                    ["AWS_CSM_CLIENT_ID"] = "CSMMARK",
                    ["AWS_REQUEST_CHECKSUM_CALCULATION"] = "when_required",
                    ["AWS_RESPONSE_CHECKSUM_VALIDATION"] = "when_required",
                    ["AWS_AUTH_SCHEME_PREFERENCE"] = "sigv4a",
                    ["AWS_DISABLE_CLOCK_SKEW_CORRECTION"] = "true",
                    ["AWS_ACCOUNT_ID"] = "111122223333",
                    ["AWS_ACCOUNT_ID_ENDPOINT_MODE"] = "required",
                    ["AWS_REGION"] = "eu-west-3",
                    ["AWS_DEFAULT_REGION"] = "eu-west-3",
                    ["AWS_S3_US_EAST_1_REGIONAL_ENDPOINT"] = "legacy",
                    ["AWS_S3_DISABLE_MULTIREGION_ACCESS_POINTS"] = "false",
                    ["AWS_S3_DISABLE_EXPRESS_SESSION_AUTH"] = "true",
                    ["AWS_ACCESS_KEY_ID"] = "AKIDAMBIENTMARK",
                    ["AWS_SECRET_ACCESS_KEY"] = "ambient",
                    ["AWS_SESSION_TOKEN"] = "TOKENMARK",
                    ["HTTP_PROXY"] = proxy,
                    ["HTTPS_PROXY"] = proxy,
                });
                break;
            case "variables-fips-dualstack":
                Set(env, new() { ["AWS_USE_FIPS_ENDPOINT"] = "true", ["AWS_USE_DUALSTACK_ENDPOINT"] = "true" });
                break;
            case "variables-us-east-1-legacy":
                Set(env, new() { ["AWS_S3_US_EAST_1_REGIONAL_ENDPOINT"] = "legacy" });
                break;
            case "variables-defaults-mode":
                // The variable's spelling of profile-defaults-mode: "in-region" is refused by this SDK (measured).
                Set(env, new() { ["AWS_DEFAULTS_MODE"] = "in-region" });
                break;
            case "variables-lambda-internal-env":
                // Inside AWS Lambda the SDK adds x-amzn-trace-id from _X_AMZN_TRACE_ID (only when AWS_LAMBDA_FUNCTION_NAME
                // is set), and inserts AWS_INTERNAL_ENV's text verbatim into the User-Agent (spec Annexe C12, measured).
                Set(env, new()
                {
                    ["AWS_LAMBDA_FUNCTION_NAME"] = "fnMARK",
                    ["_X_AMZN_TRACE_ID"] = "Root=1-00000000-TRACEMARK",
                    ["AWS_INTERNAL_ENV"] = "IENVMARK",
                });
                break;
            case "variables-imds":
                // The instance-metadata service, switched ON (the clean environment turns it off) and pointed at the "evil"
                // listener, a loopback port the child can reach. The provider is given explicit credentials and an explicit
                // endpoint, so it must never contact it: no ambient credential or region lookup may ride on the SDK's fallback
                // chain. The plain witness (no credentials given) does (AssertWitnesses).
                env["AWS_EC2_METADATA_DISABLED"] = null;
                Set(env, new() { ["AWS_EC2_METADATA_SERVICE_ENDPOINT"] = evil });
                break;
            case "profile":
                // A NAMED profile (AWS_PROFILE is honoured too: the witness shows its marker). Set but not observable here
                // (measured, as for the variables): auth_scheme_preference, region, the credentials, the access-point
                // switches, and s3_us_east_1_regional_endpoint (masked by endpoint_url).
                env["AWS_CONFIG_FILE"] = Profile(
                    $"endpoint_url = {evil}", "ignore_configured_endpoint_urls = false", "sdk_ua_app_id = FILEMARK",
                    "retry_mode = adaptive", "max_attempts = 7", "request_checksum_calculation = when_required",
                    "response_checksum_validation = when_required", "csm_enabled = true", $"csm_port = {udp}", "csm_host = 127.0.0.1",
                    "csm_client_id = CSMMARK", "auth_scheme_preference = sigv4a", "region = eu-west-3", "account_id_endpoint_mode = required",
                    "s3_us_east_1_regional_endpoint = legacy", "s3_disable_multiregion_access_points = false",
                    "aws_access_key_id = AKIDPROFILEMARK", "aws_secret_access_key = profile", "aws_session_token = TOKENMARK");
                env["AWS_PROFILE"] = HostileProfile;
                break;
            case "profile-fips-dualstack":
                env["AWS_CONFIG_FILE"] = Profile("use_fips_endpoint = true", "use_dualstack_endpoint = true");
                env["AWS_PROFILE"] = HostileProfile;
                break;
            case "profile-defaults-mode":
                // "in-region" is the AWS CLI's spelling, which this SDK refuses (measured): an unguarded client fails.
                env["AWS_CONFIG_FILE"] = Profile("defaults_mode = in-region");
                env["AWS_PROFILE"] = HostileProfile;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hostileCase), hostileCase, null);
        }

        return env;

        string Profile(params string[] settings)
        {
            var path = Path.Combine(baseline.Home.Path, hostileCase + ".config");
            File.WriteAllText(path, $"[profile {HostileProfile}]\n" + string.Join("\n", settings) + "\n");
            return path;
        }
    }

    /// <summary>
    /// One assertion per effect, never a disjunction of effects. Each is a CHANGE: the clean witness run's opposite of every
    /// effect is asserted once, in <see cref="The_clean_runs_are_what_the_witnesses_compare_against"/>.
    /// </summary>
    private void AssertWitnesses(string hostileCase, string[] hostile)
    {
        var s3 = Text(baseline.S3);
        var evil = Text(baseline.Evil);
        switch (hostileCase)
        {
            case "variables":
                {
                    var ua = Single(hostile, "ua");

                    // CSM: 6 datagrams measured (2 per call, 3 calls); 0 in the clean run.
                    Assert.True(Datagrams(hostile) > 0, "CSM: the plain client sent no datagram under AWS_CSM_ENABLED");

                    // Proxy: the request to the configured loopback endpoint goes through HTTP_PROXY (absolute-form request line).
                    Assert.StartsWith($"proxy | GET http://127.0.0.1:{s3}/okf/kb/ua.md HTTP/1.1 |", ua, StringComparison.Ordinal);

                    // Endpoint: a region-configured client is sent to AWS_ENDPOINT_URL_S3 (here through the proxy as well);
                    // in the clean run, to s3.us-east-1.amazonaws.com (refused by the witness's safety net).
                    Assert.StartsWith($"proxy | GET http://127.0.0.1:{evil}/okf/kb/endpoint.md HTTP/1.1 |", Single(hostile, "endpoint"), StringComparison.Ordinal);

                    // User-Agent: AWS_SDK_UA_APP_ID and AWS_EXECUTION_ENV each add their token.
                    Assert.Contains(" app/ENVMARK ", ua, StringComparison.Ordinal);
                    Assert.Contains(" exec-env/EXECMARK ", ua, StringComparison.Ordinal);

                    // Retry: AWS_MAX_ATTEMPTS reaches the wire (max=3 in the clean run).
                    Assert.Contains("amz-sdk-request=attempt=1; max=9", ua, StringComparison.Ordinal);

                    // Retry mode: AWS_RETRY_MODE=adaptive is reported in the User-Agent's business metrics as F (E, the
                    // standard mode, in the clean run; measured alone: m/b,P,e,N,E -> m/b,P,e,N,F).
                    Assert.Contains("F", Metrics(ua));

                    // Checksum: AWS_RESPONSE_CHECKSUM_VALIDATION=when_required drops x-amz-checksum-mode (ENABLED in the clean run).
                    Assert.DoesNotContain("x-amz-checksum-mode", ua, StringComparison.Ordinal);

                    // Account-id endpoint mode: AWS_ACCOUNT_ID_ENDPOINT_MODE=required is reported in the business metrics as R
                    // instead of P (measured alone, 2026-11-19: m/b,P,E -> m/b,R,E).
                    Assert.Contains("R", Metrics(ua));
                    Assert.DoesNotContain("P", Metrics(ua));
                    break;
                }

            case "variables-fips-dualstack":
            case "profile-fips-dualstack":
                // The region-configured client is sent to the FIPS dual-stack host (s3.us-east-1.amazonaws.com when clean).
                Assert.Contains("plain endpoint refused-hop=s3-fips.dualstack.us-east-1.amazonaws.com", hostile);

                // and the endpoint-configured one fails before sending: "Cannot set dual-stack in combination with a custom endpoint."
                Assert.Contains(hostile, l => l.StartsWith("plain ua=error:AmazonClientException:", StringComparison.Ordinal));
                Assert.DoesNotContain(hostile, l => l.Contains("/okf/kb/ua.md ", StringComparison.Ordinal));
                break;

            case "variables-us-east-1-legacy":
                // The region-configured client is sent to the legacy global host (s3.us-east-1.amazonaws.com when clean).
                Assert.Contains("plain endpoint refused-hop=s3.amazonaws.com", hostile);
                break;

            case "profile":
                {
                    var ua = Single(hostile, "ua");

                    // CSM from the profile: 12 datagrams measured; 0 in the clean run.
                    Assert.True(Datagrams(hostile) > 0, "CSM: the plain client sent no datagram under csm_enabled");

                    // Endpoint: endpoint_url sends a region-configured client to the evil listener (no proxy in this case).
                    Assert.StartsWith("evil | GET /okf/kb/endpoint.md HTTP/1.1 |", Single(hostile, "endpoint"), StringComparison.Ordinal);

                    // User-Agent: sdk_ua_app_id of the NAMED profile (so AWS_PROFILE is read too).
                    Assert.Contains(" app/FILEMARK ", ua, StringComparison.Ordinal);

                    // Retry: max_attempts reaches the wire.
                    Assert.Contains("amz-sdk-request=attempt=1; max=7", ua, StringComparison.Ordinal);

                    // Retry mode: retry_mode = adaptive, business metric F (E when clean).
                    Assert.Contains("F", Metrics(ua));

                    // Checksum: response_checksum_validation = when_required drops x-amz-checksum-mode.
                    Assert.DoesNotContain("x-amz-checksum-mode", ua, StringComparison.Ordinal);

                    // Account-id endpoint mode: account_id_endpoint_mode = required, business metric R (P when clean).
                    Assert.Contains("R", Metrics(ua));
                    Assert.DoesNotContain("P", Metrics(ua));
                    break;
                }

            case "variables-imds":
                // POSITIVE WITNESS: a plain client with no credentials really goes to the metadata endpoint named by the
                // environment (a request path under /latest/), so the provider's silence on that listener means something.
                Assert.Contains(hostile, l => l.StartsWith("evil | ", StringComparison.Ordinal) && l.Contains(" /latest/", StringComparison.Ordinal));
                break;

            case "variables-lambda-internal-env":
                {
                    // Measured 2026-11-19: the header carries the ambient trace id, unsigned (the clean run sends none), and the
                    // marker sits between md/aws-sdk-dotnet-core#… and api/S3#… in the User-Agent.
                    var ua = Single(hostile, "ua");
                    Assert.Contains(" | x-amzn-trace-id=Root=1-00000000-TRACEMARK", ua, StringComparison.Ordinal);
                    Assert.DoesNotContain("x-amzn-trace-id", ua.Split(" | ").Single(f => f.StartsWith("signed=", StringComparison.Ordinal)), StringComparison.Ordinal);
                    Assert.Contains(" IENVMARK api/S3#", ua, StringComparison.Ordinal);
                    break;
                }

            case "variables-defaults-mode":
            case "profile-defaults-mode":
                // The unguarded client cannot even be configured: every scenario fails, naming the defaults mode.
                Assert.Contains(hostile, l => l.StartsWith("plain ua=error:AmazonClientException:", StringComparison.Ordinal) && l.Contains("Default Configuration Mode", StringComparison.Ordinal));
                Assert.DoesNotContain(hostile, l => l.Contains("/okf/kb/ua.md ", StringComparison.Ordinal));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(hostileCase), hostileCase, null);
        }
    }

    private static string Single(string[] lines, string scenario) =>
        Assert.Single(lines, l => l.Contains($"/okf/kb/{scenario}.md HTTP/1.1 |", StringComparison.Ordinal));

    /// <summary>The User-Agent's business-metric codes (the comma-separated "m/…" token), e.g. ["b", "P", "e", "N", "E"].</summary>
    private static string[] Metrics(string recorded) =>
        Assert.Single(recorded.Split(' '), t => t.StartsWith("m/", StringComparison.Ordinal))["m/".Length..].Split(',');

    private static int Datagrams(string[] lines) =>
        int.Parse(Assert.Single(lines, l => l.StartsWith("UDP ", StringComparison.Ordinal))["UDP ".Length..], CultureInfo.InvariantCulture);

    private static void Set(Dictionary<string, string?> env, Dictionary<string, string> values)
    {
        foreach (var (k, v) in values)
        {
            env[k] = v;
        }
    }

    private static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);
}
