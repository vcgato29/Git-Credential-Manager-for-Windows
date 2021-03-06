﻿/**** Git Credential Manager for Windows ****
 *
 * Copyright (c) Microsoft Corporation
 * All rights reserved.
 *
 * MIT License
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the """"Software""""), to deal
 * in the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
 * the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
 * AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."
**/

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Alm.Authentication;
using Microsoft.Alm.Git;
using Bitbucket = Atlassian.Bitbucket.Authentication;
using Github = GitHub.Authentication;

namespace Microsoft.Alm.Cli
{
    internal static class CommonFunctions
    {
        public static async Task<BaseAuthentication> CreateAuthentication(Program program, OperationArguments operationArguments)
        {
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));
            if (operationArguments.TargetUri is null)
            {
                var innerException = new NullReferenceException($"`{operationArguments.TargetUri}` cannot be null.");
                throw new ArgumentException(innerException.Message, nameof(operationArguments), innerException);
            }

            var secretsNamespace = operationArguments.CustomNamespace ?? Program.SecretsNamespace;
            var secrets = new SecretStore(secretsNamespace, null, null, Secret.UriToName);
            BaseAuthentication authority = null;

            var basicCredentialCallback = (operationArguments.UseModalUi)
                    ? new AcquireCredentialsDelegate(program.ModalPromptForCredentials)
                    : new AcquireCredentialsDelegate(program.BasicCredentialPrompt);

            var bitbucketCredentialCallback = (operationArguments.UseModalUi)
                    ? Bitbucket.AuthenticationPrompts.CredentialModalPrompt
                    : new Bitbucket.Authentication.AcquireCredentialsDelegate(program.BitbucketCredentialPrompt);

            var bitbucketOauthCallback = (operationArguments.UseModalUi)
                    ? Bitbucket.AuthenticationPrompts.AuthenticationOAuthModalPrompt
                    : new Bitbucket.Authentication.AcquireAuthenticationOAuthDelegate(program.BitbucketOAuthPrompt);

            var githubCredentialCallback = (operationArguments.UseModalUi)
                    ? new Github.Authentication.AcquireCredentialsDelegate(Github.AuthenticationPrompts.CredentialModalPrompt)
                    : new Github.Authentication.AcquireCredentialsDelegate(program.GitHubCredentialPrompt);

            var githubAuthcodeCallback = (operationArguments.UseModalUi)
                    ? new Github.Authentication.AcquireAuthenticationCodeDelegate(Github.AuthenticationPrompts.AuthenticationCodeModalPrompt)
                    : new Github.Authentication.AcquireAuthenticationCodeDelegate(program.GitHubAuthCodePrompt);

            NtlmSupport basicNtlmSupport = NtlmSupport.Auto;

            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;

            switch (operationArguments.Authority)
            {
                case AuthorityType.Auto:
                    Git.Trace.WriteLine($"detecting authority type for '{operationArguments.TargetUri}'.");

                    // Detect the authority.
                    authority = await BaseVstsAuthentication.GetAuthentication(operationArguments.TargetUri,
                                                                               Program.VstsCredentialScope,
                                                                               secrets)
                             ?? Github.Authentication.GetAuthentication(operationArguments.TargetUri,
                                                                        Program.GitHubCredentialScope,
                                                                        secrets,
                                                                        githubCredentialCallback,
                                                                        githubAuthcodeCallback,
                                                                        null)
                            ?? Bitbucket.Authentication.GetAuthentication(operationArguments.TargetUri,
                                                                          new SecretStore(secretsNamespace, Secret.UriToActualUrl),
                                                                          bitbucketCredentialCallback,
                                                                          bitbucketOauthCallback);

                    if (authority != null)
                    {
                        // Set the authority type based on the returned value.
                        if (authority is VstsMsaAuthentication)
                        {
                            operationArguments.Authority = AuthorityType.MicrosoftAccount;
                            goto case AuthorityType.MicrosoftAccount;
                        }
                        else if (authority is VstsAadAuthentication)
                        {
                            operationArguments.Authority = AuthorityType.AzureDirectory;
                            goto case AuthorityType.AzureDirectory;
                        }
                        else if (authority is Github.Authentication)
                        {
                            operationArguments.Authority = AuthorityType.GitHub;
                            goto case AuthorityType.GitHub;
                        }
                        else if (authority is Bitbucket.Authentication)
                        {
                            operationArguments.Authority = AuthorityType.Bitbucket;
                            goto case AuthorityType.Bitbucket;
                        }
                    }
                    goto default;

                case AuthorityType.AzureDirectory:
                    Git.Trace.WriteLine($"authority for '{operationArguments.TargetUri}' is Azure Directory.");

                    Guid tenantId = Guid.Empty;

                    // Get the identity of the tenant.
                    var result = await BaseVstsAuthentication.DetectAuthority(operationArguments.TargetUri);

                    if (result.Key)
                    {
                        tenantId = result.Value;
                    }

                    // Return the allocated authority or a generic AAD backed VSTS authentication object.
                    return authority ?? new VstsAadAuthentication(tenantId, Program.VstsCredentialScope, secrets);

                case AuthorityType.Basic:
                    // Enforce basic authentication only.
                    basicNtlmSupport = NtlmSupport.Never;
                    goto default;

                case AuthorityType.GitHub:
                    Git.Trace.WriteLine($"authority for '{operationArguments.TargetUri}' is GitHub.");

                    // Return a GitHub authentication object.
                    return authority ?? new Github.Authentication(operationArguments.TargetUri,
                                                                  Program.GitHubCredentialScope,
                                                                  secrets,
                                                                  githubCredentialCallback,
                                                                  githubAuthcodeCallback,
                                                                  null);

                case AuthorityType.Bitbucket:
                    Git.Trace.WriteLine($"authority for '{operationArguments.TargetUri}'  is Bitbucket");

                    // Return a Bitbucket authentication object.
                    return authority ?? new Bitbucket.Authentication(secrets,
                                                                     bitbucketCredentialCallback,
                                                                     bitbucketOauthCallback);

                case AuthorityType.MicrosoftAccount:
                    Git.Trace.WriteLine($"authority for '{operationArguments.TargetUri}' is Microsoft Live.");

                    // Return the allocated authority or a generic MSA backed VSTS authentication object.
                    return authority ?? new VstsMsaAuthentication(Program.VstsCredentialScope, secrets);

                case AuthorityType.Ntlm:
                    // Enforce NTLM authentication only.
                    basicNtlmSupport = NtlmSupport.Always;
                    goto default;

                default:
                    Git.Trace.WriteLine($"authority for '{operationArguments.TargetUri}' is basic with NTLM={basicNtlmSupport}.");

                    // Return a generic username + password authentication object.
                    return authority ?? new BasicAuthentication(secrets, basicNtlmSupport, basicCredentialCallback, null);
            }
        }

        public static void DeleteCredentials(Program program, OperationArguments operationArguments)
        {
            if (operationArguments is null)
                throw new ArgumentNullException("operationArguments");

            var task = Task.Run(async () => { return await program.CreateAuthentication(operationArguments); });

            BaseAuthentication authentication = task.Result;

            switch (operationArguments.Authority)
            {
                default:
                case AuthorityType.Basic:
                    Git.Trace.WriteLine($"deleting basic credentials for '{operationArguments.TargetUri}'.");
                    authentication.DeleteCredentials(operationArguments.TargetUri);
                    break;

                case AuthorityType.AzureDirectory:
                case AuthorityType.MicrosoftAccount:
                    Git.Trace.WriteLine($"deleting VSTS credentials for '{operationArguments.TargetUri}'.");
                    var vstsAuth = authentication as BaseVstsAuthentication;
                    vstsAuth.DeleteCredentials(operationArguments.TargetUri);
                    break;

                case AuthorityType.GitHub:
                    Git.Trace.WriteLine($"deleting GitHub credentials for '{operationArguments.TargetUri}'.");
                    var ghAuth = authentication as Github.Authentication;
                    ghAuth.DeleteCredentials(operationArguments.TargetUri);
                    break;

                case AuthorityType.Bitbucket:
                    Git.Trace.WriteLine($"deleting Bitbucket credentials for '{operationArguments.TargetUri}'.");
                    var bbAuth = authentication as Bitbucket.Authentication;
                    bbAuth.DeleteCredentials(operationArguments.TargetUri, operationArguments.CredUsername);
                    break;
            }
        }

        public static void DieException(Program program, Exception exception, string path, int line, string name)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (exception is null)
                throw new ArgumentNullException(nameof(exception));

            Git.Trace.WriteLine(exception.ToString(), path, line, name);
            program.LogEvent(exception.ToString(), EventLogEntryType.Error);

            string message;
            if (!string.IsNullOrWhiteSpace(exception.Message))
            {
                message = $"{exception.GetType().Name} encountered.\n   {exception.Message}";
            }
            else
            {
                message = $"{exception.GetType().Name} encountered.";
            }

            program.Die(message, path, line, name);
        }

        public static void DieMessage(Program program, string message, string path, int line, string name)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            message = $"fatal: {message}";

            program.Exit(-1, message, path, line, name);
        }

        public static void EnableTraceLogging(Program program, OperationArguments operationArguments)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));

            if (operationArguments.WriteLog)
            {
                Git.Trace.WriteLine("trace logging enabled.");

                string gitConfigPath;
                if (Where.GitLocalConfig(out gitConfigPath))
                {
                    Git.Trace.WriteLine($"git local config found at '{gitConfigPath}'.");

                    string gitDirPath = Path.GetDirectoryName(gitConfigPath);

                    if (Directory.Exists(gitDirPath))
                    {
                        program.EnableTraceLogging(operationArguments, gitDirPath);
                    }
                }
                else if (Where.GitGlobalConfig(out gitConfigPath))
                {
                    Git.Trace.WriteLine($"git global config found at '{gitConfigPath}'.");

                    string homeDirPath = Path.GetDirectoryName(gitConfigPath);

                    if (Directory.Exists(homeDirPath))
                    {
                        program.EnableTraceLogging(operationArguments, homeDirPath);
                    }
                }
            }
#if DEBUG
            Git.Trace.WriteLine($"GCM arguments:{Environment.NewLine}{operationArguments}");
#endif
        }

        public static void EnableTraceLoggingFile(Program program, OperationArguments operationArguments, string logFilePath)
        {
            const int LogFileMaxLength = 8 * 1024 * 1024; // 8 MB

            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));
            if (logFilePath is null)
                throw new ArgumentNullException(nameof(logFilePath));

            string logFileName = Path.Combine(logFilePath, Path.ChangeExtension(Program.ConfigPrefix, ".log"));

            var logFileInfo = new FileInfo(logFileName);
            if (logFileInfo.Exists && logFileInfo.Length > LogFileMaxLength)
            {
                for (int i = 1; i < int.MaxValue; i++)
                {
                    string moveName = string.Format("{0}{1:000}.log", Program.ConfigPrefix, i);
                    string movePath = Path.Combine(logFilePath, moveName);

                    if (!File.Exists(movePath))
                    {
                        logFileInfo.MoveTo(movePath);
                        break;
                    }
                }
            }

            Git.Trace.WriteLine($"trace log destination is '{logFilePath}'.");

            using (var fileStream = File.Open(logFileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var listener = new StreamWriter(fileStream, Encoding.UTF8);
                Git.Trace.AddListener(listener);

                // write a small header to help with identifying new log entries
                listener.Write('\n');
                listener.Write($"{DateTime.Now:yyyy.MM.dd HH:mm:ss} Microsoft {program.Title} version {program.Version.ToString(3)}\n");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Scope = "member", Target = "Microsoft.Alm.Cli.CommonFunctions.#LoadOperationArguments(Microsoft.Alm.Cli.Program,Microsoft.Alm.Cli.OperationArguments)")]
        public static void LoadOperationArguments(Program program, OperationArguments operationArguments)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));

            if (operationArguments.TargetUri == null)
            {
                program.Die("No host information, unable to continue.");
            }

            string value;
            bool? yesno;

            if (program.TryReadBoolean(operationArguments, null, Program.EnvironConfigNoLocalKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.EnvironConfigNoLocalKey} = '{yesno}'.");

                operationArguments.UseConfigLocal = yesno.Value;
            }

            if (program.TryReadBoolean(operationArguments, null, Program.EnvironConfigNoSystemKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.EnvironConfigNoSystemKey} = '{yesno}'.");

                operationArguments.UseConfigSystem = yesno.Value;
            }

            // Load/re-load the Git configuration after setting the use local/system config values.
            operationArguments.LoadConfiguration();

            // If a user-agent has been specified in the environment, set it globally.
            if (program.TryReadString(operationArguments, null, Program.EnvironHttpUserAgent, out value))
            {
                Git.Trace.WriteLine($"{Program.EnvironHttpUserAgent} = '{value}'.");

                Global.UserAgent = value;
            }

            // Look for authority settings.
            if (program.TryReadString(operationArguments, Program.ConfigAuthorityKey, Program.EnvironAuthorityKey, out value))
            {
                Git.Trace.WriteLine($"{Program.ConfigAuthorityKey} = '{value}'.");

                if (Program.ConfigKeyComparer.Equals(value, "MSA")
                    || Program.ConfigKeyComparer.Equals(value, "Microsoft")
                    || Program.ConfigKeyComparer.Equals(value, "MicrosoftAccount")
                    || Program.ConfigKeyComparer.Equals(value, "Live")
                    || Program.ConfigKeyComparer.Equals(value, "LiveConnect")
                    || Program.ConfigKeyComparer.Equals(value, "LiveID"))
                {
                    operationArguments.Authority = AuthorityType.MicrosoftAccount;
                }
                else if (Program.ConfigKeyComparer.Equals(value, "AAD")
                         || Program.ConfigKeyComparer.Equals(value, "Azure")
                         || Program.ConfigKeyComparer.Equals(value, "AzureDirectory"))
                {
                    operationArguments.Authority = AuthorityType.AzureDirectory;
                }
                else if (Program.ConfigKeyComparer.Equals(value, "Integrated")
                         || Program.ConfigKeyComparer.Equals(value, "Windows")
                         || Program.ConfigKeyComparer.Equals(value, "TFS")
                         || Program.ConfigKeyComparer.Equals(value, "Kerberos")
                         || Program.ConfigKeyComparer.Equals(value, "NTLM")
                         || Program.ConfigKeyComparer.Equals(value, "SSO"))
                {
                    operationArguments.Authority = AuthorityType.Ntlm;
                }
                else if (Program.ConfigKeyComparer.Equals(value, "GitHub"))
                {
                    operationArguments.Authority = AuthorityType.GitHub;
                }
                else if (Program.ConfigKeyComparer.Equals(value, "Atlassian")
                    || Program.ConfigKeyComparer.Equals(value, "Bitbucket"))
                {
                    operationArguments.Authority = AuthorityType.Bitbucket;
                }
                else
                {
                    operationArguments.Authority = AuthorityType.Basic;
                }
            }

            // Look for interactivity config settings.
            if (program.TryReadString(operationArguments, Program.ConfigInteractiveKey, Program.EnvironInteractiveKey, out value))
            {
                Git.Trace.WriteLine($"{Program.EnvironInteractiveKey} = '{value}'.");

                if (Program.ConfigKeyComparer.Equals(value, "always")
                    || Program.ConfigKeyComparer.Equals(value, "true")
                    || Program.ConfigKeyComparer.Equals(value, "force"))
                {
                    operationArguments.Interactivity = Interactivity.Always;
                }
                else if (Program.ConfigKeyComparer.Equals(value, "never")
                         || Program.ConfigKeyComparer.Equals(value, "false"))
                {
                    operationArguments.Interactivity = Interactivity.Never;
                }
            }

            // Look for credential validation config settings.
            if (program.TryReadBoolean(operationArguments, Program.ConfigValidateKey, Program.EnvironValidateKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.ConfigValidateKey} = '{yesno}'.");

                operationArguments.ValidateCredentials = yesno.Value;
            }

            // Look for write log config settings.
            if (program.TryReadBoolean(operationArguments, Program.ConfigWritelogKey, Program.EnvironWritelogKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.ConfigWritelogKey} = '{yesno}'.");

                operationArguments.WriteLog = yesno.Value;
            }

            // Look for modal prompt config settings.
            if (program.TryReadBoolean(operationArguments, Program.ConfigUseModalPromptKey, Program.EnvironModalPromptKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.ConfigUseModalPromptKey} = '{yesno}'.");

                operationArguments.UseModalUi = yesno.Value;
            }

            // Look for credential preservation config settings.
            if (program.TryReadBoolean(operationArguments, Program.ConfigPreserveCredentialsKey, Program.EnvironPreserveCredentialsKey, out yesno))
            {
                Git.Trace.WriteLine($"{Program.ConfigPreserveCredentialsKey} = '{yesno}'.");

                operationArguments.PreserveCredentials = yesno.Value;
            }

            // Look for HTTP path usage config settings.
            if (program.TryReadBoolean(operationArguments, Program.ConfigUseHttpPathKey, null, out yesno))
            {
                Git.Trace.WriteLine($"{Program.ConfigUseHttpPathKey} = '{value}'.");

                operationArguments.UseHttpPath = yesno.Value;
            }

            // Look for HTTP proxy config settings.
            if (program.TryReadString(operationArguments, Program.ConfigHttpProxyKey, Program.EnvironHttpProxyKey, out value))
            {
                Git.Trace.WriteLine($"{Program.ConfigHttpProxyKey} = '{value}'.");

                operationArguments.SetProxy(value);
            }
            else
            {
                // Check the git-config http.proxy setting just-in-case.
                Configuration.Entry entry;
                if (operationArguments.GitConfiguration.TryGetEntry("http", operationArguments.QueryUri, "proxy", out entry)
                    && !string.IsNullOrWhiteSpace(entry.Value))
                {
                    Git.Trace.WriteLine($"http.proxy = '{entry.Value}'.");

                    operationArguments.SetProxy(entry.Value);
                }
            }

            // Look for custom namespace config settings.
            if (program.TryReadString(operationArguments, Program.ConfigNamespaceKey, Program.EnvironNamespaceKey, out value))
            {
                Git.Trace.WriteLine($"{Program.ConfigNamespaceKey} = '{value}'.");

                operationArguments.CustomNamespace = value;
            }

            // Look for custom token duration settings.
            if (program.TryReadString(operationArguments, Program.ConfigTokenDuration, Program.EnvironTokenDuration, out value))
            {
                Git.Trace.WriteLine($"{Program.ConfigTokenDuration} = '{value}'.");

                int hours;
                if (int.TryParse(value, out hours))
                {
                    operationArguments.TokenDuration = TimeSpan.FromHours(hours);
                }
            }
        }

        public static void LogEvent(Program program, string message, EventLogEntryType eventType)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            /*** try-squelch due to UAC issues which require a proper installer to work around ***/

            Git.Trace.WriteLine(message);

            try
            {
                EventLog.WriteEntry(Program.EventSource, message, eventType);
            }
            catch { /* squelch */ }
        }

        public static void PrintArgs(Program program, string[] args)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (args is null)
                throw new ArgumentNullException(nameof(args));

            var builder = new StringBuilder();
            builder.Append(program.Name)
                   .Append(" (v")
                   .Append(program.Version.ToString(3))
                   .Append(")");

            for (int i = 0; i < args.Length; i += 1)
            {
                builder.Append(" '")
                       .Append(args[i])
                       .Append("'");

                if (i + 1 < args.Length)
                {
                    builder.Append(",");
                }
            }

            // Fake being part of the Main method for clarity.
            Git.Trace.WriteLine(builder.ToString(), memberName: "Main");
            builder = null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Scope = "member", Target = "Microsoft.Alm.Cli.CommonFunctions.#QueryCredentials(Microsoft.Alm.Cli.Program,Microsoft.Alm.Cli.OperationArguments)")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Scope = "member", Target = "Microsoft.Alm.Cli.CommonFunctions.#QueryCredentials(Microsoft.Alm.Cli.Program,Microsoft.Alm.Cli.OperationArguments)")]
        public static Credential QueryCredentials(Program program, OperationArguments operationArguments)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));
            if (operationArguments.TargetUri is null)
            {
                var innerException = new NullReferenceException($"{operationArguments.TargetUri} cannot be null.");
                throw new ArgumentException(innerException.Message, nameof(operationArguments), innerException);
            }

            var task = Task.Run(async () => { return await program.CreateAuthentication(operationArguments); });
            BaseAuthentication authentication = task.Result;
            Credential credentials = null;

            switch (operationArguments.Authority)
            {
                default:
                case AuthorityType.Basic:
                    {
                        var basicAuth = authentication as BasicAuthentication;

                        Task.Run(async () =>
                        {
                            // Attempt to get cached credentials or acquire credentials if interactivity is allowed.
                            if ((operationArguments.Interactivity != Interactivity.Always
                                    && (credentials = authentication.GetCredentials(operationArguments.TargetUri)) != null)
                                || (operationArguments.Interactivity != Interactivity.Never
                                    && (credentials = await basicAuth.AcquireCredentials(operationArguments.TargetUri)) != null))
                            {
                                Git.Trace.WriteLine("credentials found.");
                                // No need to save the credentials explicitly, as Git will call back
                                // with a store command if the credentials are valid.
                            }
                            else
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' not found.");
                                program.LogEvent($"Failed to retrieve credentials for '{operationArguments.TargetUri}'.", EventLogEntryType.FailureAudit);
                            }
                        }).Wait();
                    }
                    break;

                case AuthorityType.AzureDirectory:
                    {
                        var aadAuth = authentication as VstsAadAuthentication;
                        var patOptions = new PersonalAccessTokenOptions()
                        {
                            RequireCompactToken = true,
                            TokenDuration = operationArguments.TokenDuration,
                            TokenScope = null,
                        };

                        Task.Run(async () =>
                        {
                            // Attempt to get cached credentials -> non-interactive logon -> interactive
                            // logon note that AAD "credentials" are always scoped access tokens.
                            if (((operationArguments.Interactivity != Interactivity.Always
                                    && ((credentials = aadAuth.GetCredentials(operationArguments.TargetUri)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await aadAuth.ValidateCredentials(operationArguments.TargetUri, credentials))))
                                || (operationArguments.Interactivity != Interactivity.Always
                                    && ((credentials = await aadAuth.NoninteractiveLogon(operationArguments.TargetUri, patOptions)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await aadAuth.ValidateCredentials(operationArguments.TargetUri, credentials)))
                                || (operationArguments.Interactivity != Interactivity.Never
                                    && ((credentials = await aadAuth.InteractiveLogon(operationArguments.TargetUri, patOptions)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await aadAuth.ValidateCredentials(operationArguments.TargetUri, credentials))))
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' found.");
                                program.LogEvent($"Azure Directory credentials  for '{operationArguments.TargetUri}' successfully retrieved.", EventLogEntryType.SuccessAudit);
                            }
                            else
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' not found.");
                                program.LogEvent($"Failed to retrieve Azure Directory credentials for '{operationArguments.TargetUri}'.", EventLogEntryType.FailureAudit);
                            }
                        }).Wait();
                    }
                    break;

                case AuthorityType.MicrosoftAccount:
                    {
                        var msaAuth = authentication as VstsMsaAuthentication;
                        var patOptions = new PersonalAccessTokenOptions()
                        {
                            RequireCompactToken = true,
                            TokenDuration = operationArguments.TokenDuration,
                            TokenScope = null,
                        };

                        Task.Run(async () =>
                        {
                            // Attempt to get cached credentials -> interactive logon note that MSA
                            // "credentials" are always scoped access tokens.
                            if (((operationArguments.Interactivity != Interactivity.Always
                                    && ((credentials = msaAuth.GetCredentials(operationArguments.TargetUri)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await msaAuth.ValidateCredentials(operationArguments.TargetUri, credentials))))
                                || (operationArguments.Interactivity != Interactivity.Never
                                    && ((credentials = await msaAuth.InteractiveLogon(operationArguments.TargetUri, patOptions)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await msaAuth.ValidateCredentials(operationArguments.TargetUri, credentials))))
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' found.");
                                program.LogEvent($"Microsoft Live credentials for '{operationArguments.TargetUri}' successfully retrieved.", EventLogEntryType.SuccessAudit);
                            }
                            else
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' not found.");
                                program.LogEvent($"Failed to retrieve Microsoft Live credentials for '{operationArguments.TargetUri}'.", EventLogEntryType.FailureAudit);
                            }
                        }).Wait();
                    }
                    break;

                case AuthorityType.GitHub:
                    {
                        var ghAuth = authentication as Github.Authentication;

                        Task.Run(async () =>
                        {
                            if ((operationArguments.Interactivity != Interactivity.Always
                                    && ((credentials = ghAuth.GetCredentials(operationArguments.TargetUri)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await ghAuth.ValidateCredentials(operationArguments.TargetUri, credentials)))
                                || (operationArguments.Interactivity != Interactivity.Never
                                    && ((credentials = await ghAuth.InteractiveLogon(operationArguments.TargetUri)) != null)
                                    && (!operationArguments.ValidateCredentials
                                        || await ghAuth.ValidateCredentials(operationArguments.TargetUri, credentials))))
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' found.");
                                program.LogEvent($"GitHub credentials for '{operationArguments.TargetUri}' successfully retrieved.", EventLogEntryType.SuccessAudit);
                            }
                            else
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' not found.");
                                program.LogEvent($"Failed to retrieve GitHub credentials for '{operationArguments.TargetUri}'.", EventLogEntryType.FailureAudit);
                            }
                        }).Wait();
                    }
                    break;

                case AuthorityType.Bitbucket:
                    {
                        var bbcAuth = authentication as Bitbucket.Authentication;

                        Task.Run(async () =>
                        {
                            if (((operationArguments.Interactivity != Interactivity.Always)
                                 && ((credentials = bbcAuth.GetCredentials(operationArguments.TargetUri, operationArguments.CredUsername)) != null)
                                 && (!operationArguments.ValidateCredentials
                                     || ((credentials = await bbcAuth.ValidateCredentials(operationArguments.TargetUri, operationArguments.CredUsername, credentials)) != null)))
                                     || ((operationArguments.Interactivity != Interactivity.Never)
                                        && ((credentials = await bbcAuth.InteractiveLogon(operationArguments.TargetUri, operationArguments.CredUsername)) != null)
                                        && (!operationArguments.ValidateCredentials
                                            || ((credentials = await bbcAuth.ValidateCredentials(operationArguments.TargetUri, operationArguments.CredUsername, credentials)) != null))))
                            {
                                Git.Trace.WriteLine($"credentials for '{operationArguments.TargetUri}' found.");
                                // Bitbucket relies on a username + secret, so make sure there is a
                                // username to return.
                                if (operationArguments.CredUsername != null)
                                {
                                    credentials = new Credential(operationArguments.CredUsername, credentials.Password);
                                }
                                program.LogEvent($"Bitbucket credentials for '{operationArguments.TargetUri}' successfully retrieved.", EventLogEntryType.SuccessAudit);
                            }
                            else
                            {
                                program.LogEvent($"Failed to retrieve Bitbucket credentials for '{operationArguments.TargetUri}'.", EventLogEntryType.FailureAudit);
                            }
                        }).Wait();
                    }
                    break;

                case AuthorityType.Ntlm:
                    {
                        Git.Trace.WriteLine($"'{operationArguments.TargetUri}' is NTLM.");
                        credentials = BasicAuthentication.NtlmCredentials;
                    }
                    break;
            }

            if (credentials != null)
            {
                operationArguments.SetCredentials(credentials);
            }

            return credentials;
        }

        public static bool TryReadBoolean(Program program, OperationArguments operationArguments, string configKey, string environKey, out bool? value)
        {
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));

            var envars = operationArguments.EnvironmentVariables;

            // Look for an entry in the environment variables.
            string localVal = null;
            if (!string.IsNullOrWhiteSpace(environKey)
                && envars.TryGetValue(environKey, out localVal))
            {
                goto parse_localval;
            }

            var config = operationArguments.GitConfiguration;

            // Look for an entry in the git config.
            Configuration.Entry entry;
            if (!string.IsNullOrWhiteSpace(configKey)
                && config.TryGetEntry(Program.ConfigPrefix, operationArguments.QueryUri, configKey, out entry))
            {
                localVal = entry.Value;
                goto parse_localval;
            }

            // Parse the value into a bool.
            parse_localval:

            // An empty value is unset / should not be there, so treat it as if it isn't.
            if (string.IsNullOrWhiteSpace(localVal))
            {
                value = null;
                return false;
            }

            // Test `localValue` for a Git 'true' equivalent value.
            if (Program.ConfigValueComparer.Equals(localVal, "yes")
                || Program.ConfigValueComparer.Equals(localVal, "true")
                || Program.ConfigValueComparer.Equals(localVal, "1")
                || Program.ConfigValueComparer.Equals(localVal, "on"))
            {
                value = true;
                return true;
            }

            // Test `localValue` for a Git 'false' equivalent value.
            if (Program.ConfigValueComparer.Equals(localVal, "no")
                || Program.ConfigValueComparer.Equals(localVal, "false")
                || Program.ConfigValueComparer.Equals(localVal, "0")
                || Program.ConfigValueComparer.Equals(localVal, "off"))
            {
                value = false;
                return true;
            }

            value = null;
            return false;
        }

        public static bool TryReadString(Program program, OperationArguments operationArguments, string configKey, string environKey, out string value)
        {
            if (operationArguments is null)
                throw new ArgumentNullException(nameof(operationArguments));

            var envars = operationArguments.EnvironmentVariables;

            // Look for an entry in the environment variables.
            string localVal;
            if (!string.IsNullOrWhiteSpace(environKey)
                && envars.TryGetValue(environKey, out localVal)
                && !string.IsNullOrWhiteSpace(localVal))
            {
                value = localVal;
                return true;
            }

            Configuration config = operationArguments.GitConfiguration;

            // Look for an entry in the git config.
            Configuration.Entry entry;
            if (!string.IsNullOrWhiteSpace(configKey)
                && config.TryGetEntry(Program.ConfigPrefix, operationArguments.QueryUri, configKey, out entry)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                value = entry.Value;
                return true;
            }

            value = null;
            return false;
        }
    }
}
