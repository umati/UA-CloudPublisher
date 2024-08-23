
namespace Opc.Ua.Cloud.Publisher.Controllers
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Opc.Ua;
    using Opc.Ua.Client;
    using Opc.Ua.Cloud.Publisher;
    using Opc.Ua.Cloud.Publisher.Interfaces;
    using Opc.Ua.Cloud.Publisher.Models;
    using Opc.Ua.Gds.Client;
    using Opc.Ua.Security.Certificates;
    using Opc.Ua.Server;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class BrowserController : Controller
    {
        private readonly IUAApplication _app;
        private readonly OpcSessionHelper _helper;
        private readonly ILogger _logger;

        public BrowserController(OpcSessionHelper helper, IUAApplication app, ILoggerFactory loggerFactory)
        {
            _app = app;
            _helper = helper;
            _logger = loggerFactory.CreateLogger("BrowserController");
        }

        [HttpPost]
        public IActionResult DownloadUACert()
        {
            try
            {
                return File(_helper.GetCert().Export(X509ContentType.Cert), "APPLICATION/octet-stream", "cert.der");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not download UA cert file");
                return View("Index", new string[] { "Error:" + ex.Message });
            }
        }

        [HttpGet]
        public ActionResult Index()
        {
            SessionModel sessionModel = new SessionModel();
            sessionModel.SessionId = HttpContext.Session.Id;

            OpcSessionCacheData entry = null;
            if (_helper.OpcSessionCache.TryGetValue(HttpContext.Session.Id, out entry))
            {
                sessionModel.EndpointUrl = entry.EndpointURL;
                HttpContext.Session.SetString("EndpointUrl", entry.EndpointURL);
                return View("Browse", sessionModel);
            }

            return View("Index", sessionModel);
        }

        [HttpPost]
        public ActionResult UserPassword(string endpointUrl)
        {
            return View("User", new SessionModel { EndpointUrl = endpointUrl });
        }

        [HttpPost]
        public async Task<ActionResult> ConnectAsync(string username, string password, string endpointUrl)
        {
            SessionModel sessionModel = new SessionModel { UserName = username, Password = password, EndpointUrl = endpointUrl };

            Client.Session session = null;

            try
            {
                session = await _helper.GetSessionAsync(HttpContext.Session.Id, endpointUrl, username, password).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sessionModel.StatusMessage = ex.Message;
                return View("Index", sessionModel);
            }

            if (string.IsNullOrEmpty(endpointUrl))
            {
                sessionModel.StatusMessage = "The endpoint URL specified is invalid!";
                return View("Index", sessionModel);
            }
            else
            {
                HttpContext.Session.SetString("EndpointUrl", endpointUrl);
            }

            if (!string.IsNullOrEmpty(username) && (password != null))
            {
                HttpContext.Session.SetString("UserName", username);
                HttpContext.Session.SetString("Password", password);
            }
            else
            {
                HttpContext.Session.Remove("UserName");
                HttpContext.Session.Remove("Password");
            }

            if (session == null)
            {
                sessionModel.StatusMessage = "Unable to create session!";
                return View("Index", sessionModel);
            }
            else
            {
                sessionModel.StatusMessage = "Connected to: " + endpointUrl;
                return View("Browse", sessionModel);
            }
        }

        [HttpPost]
        public ActionResult Disconnect()
        {
            try
            {
                _helper.Disconnect(HttpContext.Session.Id);
                HttpContext.Session.SetString("EndpointUrl", string.Empty);
            }
            catch (Exception)
            {
                // do nothing
            }

            return View("Index", new SessionModel());
        }

        [HttpPost]
        public async Task<ActionResult> GeneratePN()
        {
            try
            {
                List<UANodeInformation> results = await BrowseNodeResursiveAsync(null).ConfigureAwait(false);

                Client.Session session = await _helper.GetSessionAsync(HttpContext.Session.Id, HttpContext.Session.GetString("EndpointUrl")).ConfigureAwait(false);

                PublishNodesInterfaceModel model = new()
                {
                    EndpointUrl = session.Endpoint.EndpointUrl,
                    OpcNodes = new List<VariableModel>()
                };

                foreach (UANodeInformation nodeInfo in results)
                {
                    if (nodeInfo.Type == "Variable")
                    {
                        VariableModel variable = new()
                        {
                            OpcSamplingInterval = 1000,
                            OpcPublishingInterval = 1000,
                            HeartbeatInterval = 0,
                            SkipFirst = false,
                            Id = nodeInfo.ExpandedNodeId
                        };

                        model.OpcNodes.Add(variable);
                    }
                }

                string json = JsonConvert.SerializeObject(new List<PublishNodesInterfaceModel>() { model }, Formatting.Indented);

                return File(Encoding.UTF8.GetBytes(json), "APPLICATION/octet-stream", "publishednodes.json");
            }
            catch (Exception ex)
            {
                SessionModel sessionModel = new SessionModel
                {
                    StatusMessage = ex.Message,
                    SessionId = HttpContext.Session.Id,
                    EndpointUrl = HttpContext.Session.GetString("EndpointUrl")
                };

                return View("Browse", sessionModel);
            }
        }

        [HttpPost]
        public async Task<ActionResult> GenerateCSV()
        {
            try
            {
                List<UANodeInformation> results = await BrowseNodeResursiveAsync(null).ConfigureAwait(false);

                string content = "Endpoint,ApplicationUri,ExpandedNodeId,DisplayName,Type,VariableCurrentValue,VariableType,Parent,References\r\n";
                foreach (UANodeInformation nodeInfo in results)
                {
                    string references = string.Empty;
                    foreach (string reference in nodeInfo.References)
                    {
                        references += reference + " | ";
                    }

                    if (references.Length > 0)
                    {
                        references = references.Substring(0, references.Length - 3);
                    }

                    content += (nodeInfo.Endpoint + ","
                              + nodeInfo.ApplicationUri + ","
                              + nodeInfo.ExpandedNodeId + ","
                              + nodeInfo.DisplayName + ","
                              + nodeInfo.Type + ","
                              + nodeInfo.VariableCurrentValue + ","
                              + nodeInfo.VariableType + ","
                              + nodeInfo.Parent + ","
                              + references + "\r\n");
                }

                return File(Encoding.UTF8.GetBytes(content), "APPLICATION/octet-stream", "opcuaservernodes.csv");
            }
            catch (Exception ex)
            {
                SessionModel sessionModel = new SessionModel
                {
                    StatusMessage = ex.Message,
                    SessionId = HttpContext.Session.Id,
                    EndpointUrl = HttpContext.Session.GetString("EndpointUrl")
                };

                return View("Browse", sessionModel);
            }
        }

        [HttpPost]
        public async Task<ActionResult> PushCert()
        {
            SessionModel sessionModel = new SessionModel
            {
                StatusMessage = string.Empty,
                SessionId = HttpContext.Session.Id,
                EndpointUrl = HttpContext.Session.GetString("EndpointUrl")
            };

            try
            {
                ServerPushConfigurationClient serverPushClient = new(_app.UAApplicationInstance.ApplicationConfiguration);

                OpcSessionCacheData entry;
                if (_helper.OpcSessionCache.TryGetValue(sessionModel.SessionId, out entry))
                {
                    if (entry.OPCSession != null)
                    {
                        serverPushClient.AdminCredentials = new UserIdentity(entry.Username, entry.Password);
                    }
                }

                await serverPushClient.Connect(sessionModel.EndpointUrl).ConfigureAwait(false);

                byte[] unusedNonce = new byte[0];
                byte[] certificateRequest = serverPushClient.CreateSigningRequest(
                    NodeId.Null,
                    serverPushClient.ApplicationCertificateType,
                string.Empty,
                false,
                unusedNonce);

                string[] domainNames = _helper.GetCert().SubjectName.Name.Split(',');

                X509Certificate2 certificate = await ProcessSigningRequestAsync(
                    _app.UAApplicationInstance.ApplicationConfiguration.ApplicationUri,
                    domainNames,
                    certificateRequest).ConfigureAwait(false);

                X509Certificate2 x509 = new X509Certificate2(certificate.Export(X509ContentType.Pfx), string.Empty, X509KeyStorageFlags.Exportable);
                byte[] privateKeyPFX = x509.Export(X509ContentType.Pfx);

                byte[][] issuerCertificates = new byte[1][];
                issuerCertificates[0] = _helper.GetCert().RawData;

                byte[] unusedPrivateKey = new byte[0];
                serverPushClient.UpdateCertificate(
                    NodeId.Null,
                    serverPushClient.ApplicationCertificateType,
                    certificate.Export(X509ContentType.Pfx),
                    (privateKeyPFX != null) ? "pfx" : string.Empty,
                    (privateKeyPFX != null) ? privateKeyPFX : unusedPrivateKey,
                    issuerCertificates);

                // store in our own trust list
                await _app.UAApplicationInstance.AddOwnCertificateToTrustedStoreAsync(certificate, CancellationToken.None).ConfigureAwait(false);

                // update trust list on server
                TrustListDataType trustList = GetTrustLists();
                serverPushClient.UpdateTrustList(trustList);

                serverPushClient.ApplyChanges();
                serverPushClient.Disconnect();
            }
            catch (Exception ex)
            {
                sessionModel.StatusMessage = ex.Message;
            }

            return View("Browse", sessionModel);
        }

        private async Task<X509Certificate2> ProcessSigningRequestAsync(string applicationUri, string[] domainNames, byte[] certificateRequest)
        {
            try
            {
                var pkcs10CertificationRequest = new Org.BouncyCastle.Pkcs.Pkcs10CertificationRequest(certificateRequest);

                if (!pkcs10CertificationRequest.Verify())
                {
                    throw new ServiceResultException(Ua.StatusCodes.BadInvalidArgument, "CSR signature invalid.");
                }

                var info = pkcs10CertificationRequest.GetCertificationRequestInfo();
                var altNameExtension = GetAltNameExtensionFromCSRInfo(info);
                if (altNameExtension != null)
                {
                    if (altNameExtension.Uris.Count > 0)
                    {
                        if (!altNameExtension.Uris.Contains(applicationUri))
                        {
                            var applicationUriMissing = new StringBuilder();
                            applicationUriMissing.AppendLine("Expected AltNameExtension (ApplicationUri):");
                            applicationUriMissing.AppendLine(applicationUri);
                            applicationUriMissing.AppendLine("CSR AltNameExtensions found:");
                            foreach (string uri in altNameExtension.Uris)
                            {
                                applicationUriMissing.AppendLine(uri);
                            }
                            throw new ServiceResultException(Ua.StatusCodes.BadCertificateUriInvalid,
                                applicationUriMissing.ToString());
                        }
                    }

                    if (altNameExtension.IPAddresses.Count > 0 || altNameExtension.DomainNames.Count > 0)
                    {
                        var domainNameList = new List<string>();
                        domainNameList.AddRange(altNameExtension.DomainNames);
                        domainNameList.AddRange(altNameExtension.IPAddresses);
                        domainNames = domainNameList.ToArray();
                    }
                }

                DateTime yesterday = DateTime.Today.AddDays(-1);
                using (var signingKey = await LoadSigningKeyAsync().ConfigureAwait(false))
                {
                    X500DistinguishedName subjectName = new X500DistinguishedName(info.Subject.GetEncoded());
                    return CertificateBuilder.Create(subjectName)
                        .AddExtension(new X509SubjectAltNameExtension(applicationUri, domainNames))
                        .SetNotBefore(yesterday)
                        .SetLifeTime(12)
                        .SetHashAlgorithm(X509Utils.GetRSAHashAlgorithmName(2048))
                        .SetIssuer(signingKey)
                        .SetRSAPublicKey(info.SubjectPublicKeyInfo.GetEncoded())
                        .CreateForRSA();
                }
            }
            catch (Exception ex)
            {
                if (ex is ServiceResultException)
                {
                    throw;
                }
                throw new ServiceResultException(Ua.StatusCodes.BadInvalidArgument, ex.Message);
            }
        }

        protected X509SubjectAltNameExtension GetAltNameExtensionFromCSRInfo(Org.BouncyCastle.Asn1.Pkcs.CertificationRequestInfo info)
        {
            try
            {
                for (int i = 0; i < info.Attributes.Count; i++)
                {
                    var sequence = Org.BouncyCastle.Asn1.Asn1Sequence.GetInstance(info.Attributes[i].ToAsn1Object());
                    var oid = Org.BouncyCastle.Asn1.DerObjectIdentifier.GetInstance(sequence[0].ToAsn1Object());

                    if (oid.Equals(Org.BouncyCastle.Asn1.Pkcs.PkcsObjectIdentifiers.Pkcs9AtExtensionRequest))
                    {
                        var extensionInstance = Org.BouncyCastle.Asn1.DerSet.GetInstance(sequence[1]);
                        var extensionSequence = Org.BouncyCastle.Asn1.Asn1Sequence.GetInstance(extensionInstance[0]);
                        var extensions = Org.BouncyCastle.Asn1.X509.X509Extensions.GetInstance(extensionSequence);
                        Org.BouncyCastle.Asn1.X509.X509Extension extension = extensions.GetExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.SubjectAlternativeName);
                        var asnEncodedAltNameExtension = new System.Security.Cryptography.AsnEncodedData(Org.BouncyCastle.Asn1.X509.X509Extensions.SubjectAlternativeName.ToString(), extension.Value.GetOctets());
                        var altNameExtension = new X509SubjectAltNameExtension(asnEncodedAltNameExtension, extension.IsCritical);
                        return altNameExtension;
                    }
                }
            }
            catch (Exception)
            {
                throw new ServiceResultException(Ua.StatusCodes.BadInvalidArgument, "CSR altNameExtension invalid.");
            }
            return null;
        }

        private async Task<X509Certificate2> LoadSigningKeyAsync()
        {
            CertificateIdentifier certIdentifier = _app.UAApplicationInstance.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;
            return await certIdentifier.LoadPrivateKey(string.Empty).ConfigureAwait(false);
        }

        private TrustListDataType GetTrustLists()
        {
            ByteStringCollection trusted = new ByteStringCollection();
            ByteStringCollection trustedCrls = new ByteStringCollection();
            ByteStringCollection issuers = new ByteStringCollection();
            ByteStringCollection issuersCrls = new ByteStringCollection();

            CertificateTrustList ownTrustList = _app.UAApplicationInstance.ApplicationConfiguration.SecurityConfiguration.TrustedPeerCertificates;
            foreach (X509Certificate2 cert in ownTrustList.GetCertificates().GetAwaiter().GetResult())
            {
                trusted.Add(cert.RawData);
            }

            issuers.Add(_helper.GetCert().RawData);

            TrustListDataType trustList = new TrustListDataType()
            {
                SpecifiedLists = (uint)(TrustListMasks.All),
                TrustedCertificates = trusted,
                TrustedCrls = trustedCrls,
                IssuerCertificates = issuers,
                IssuerCrls = issuersCrls
            };

            return trustList;
        }

        private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId)
        {
            ReferenceDescriptionCollection references = new();

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            BrowseDescription nodeToBrowse = new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = null,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            };

            BrowseDescriptionCollection nodesToBrowse = new BrowseDescriptionCollection
            {
                nodeToBrowse
            };

            try
            {
                Client.Session session = await _helper.GetSessionAsync(HttpContext.Session.Id, HttpContext.Session.GetString("EndpointUrl")).ConfigureAwait(false);

                session.Browse(
                    null,
                    null,
                    0,
                    nodesToBrowse,
                    out BrowseResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                ClientBase.ValidateResponse(results, nodesToBrowse);
                ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToBrowse);

                do
                {
                    // check for error.
                    if (Ua.StatusCode.IsBad(results[0].StatusCode))
                    {
                        break;
                    }

                    // process results.
                    for (int i = 0; i < results[0].References.Count; i++)
                    {
                        references.Add(results[0].References[i]);
                    }

                    // check if all references have been fetched.
                    if (results[0].References.Count == 0 || results[0].ContinuationPoint == null)
                    {
                        break;
                    }

                    // continue browse operation.
                    ByteStringCollection continuationPoints = new ByteStringCollection
                    {
                        results[0].ContinuationPoint
                    };

                    session.BrowseNext(
                        null,
                        false,
                        continuationPoints,
                        out results,
                        out diagnosticInfos);

                    ClientBase.ValidateResponse(results, continuationPoints);
                    ClientBase.ValidateDiagnosticInfos(diagnosticInfos, continuationPoints);
                }
                while (true);
            }
            catch (Exception ex)
            {
                _logger.LogError("Cannot browse node {0}: {1}", nodeId, ex.Message);

                throw;
            }
            finally
            {
                stopwatch.Stop();
            }

            _logger.LogInformation("Browsing all childeren info of node '{0}' took {0} ms", nodeId, stopwatch.ElapsedMilliseconds);

            return references;
        }

        private async Task<List<UANodeInformation>> BrowseNodeResursiveAsync(NodeId nodeId)
        {
            List<UANodeInformation> results = new();

            try
            {
                if (nodeId == null)
                {
                    nodeId = ObjectIds.ObjectsFolder;
                }

                ReferenceDescriptionCollection references = await BrowseNodeAsync(nodeId).ConfigureAwait(false);
                if (references != null)
                {
                    List<string> processedReferences = new();
                    foreach (ReferenceDescription nodeReference in references)
                    {
                        // filter out duplicates
                        if (processedReferences.Contains(nodeReference.NodeId.ToString()))
                        {
                            continue;
                        }

                        UANodeInformation nodeInfo = new()
                        {
                            DisplayName = nodeReference.DisplayName.Text,
                            Type = nodeReference.NodeClass.ToString()
                        };

                        try
                        {
                            Client.Session session = await _helper.GetSessionAsync(HttpContext.Session.Id, HttpContext.Session.GetString("EndpointUrl")).ConfigureAwait(false);

                            nodeInfo.ApplicationUri = session.ServerUris.ToArray()[0];

                            nodeInfo.Endpoint = session.Endpoint.EndpointUrl;

                            if (nodeId.NamespaceIndex == 0)
                            {
                                nodeInfo.Parent = "nsu=http://opcfoundation.org/UA;" + nodeId.ToString();
                            }
                            else
                            {
                                nodeInfo.Parent = NodeId.ToExpandedNodeId(ExpandedNodeId.ToNodeId(nodeId, session.NamespaceUris), session.NamespaceUris).ToString();
                            }

                            if (nodeReference.NodeId.NamespaceIndex == 0)
                            {
                                nodeInfo.ExpandedNodeId = "nsu=http://opcfoundation.org/UA;" + nodeReference.NodeId.ToString();
                            }
                            else
                            {
                                nodeInfo.ExpandedNodeId = NodeId.ToExpandedNodeId(ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris), session.NamespaceUris).ToString();
                            }

                            if (nodeReference.NodeClass == NodeClass.Variable)
                            {
                                try
                                {
                                    DataValue value = session.ReadValue(ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris));
                                    if ((value != null) && (value.WrappedValue != Variant.Null))
                                    {
                                        nodeInfo.VariableCurrentValue = value.ToString();
                                        nodeInfo.VariableType = value.WrappedValue.TypeInfo.ToString();
                                    }
                                }
                                catch (Exception)
                                {
                                    // do nothing
                                }
                            }

                            List<UANodeInformation> childReferences = await BrowseNodeResursiveAsync(ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris)).ConfigureAwait(false);

                            nodeInfo.References = new string[childReferences.Count];
                            for (int i = 0; i < childReferences.Count; i++)
                            {
                                nodeInfo.References[i] = childReferences[i].ExpandedNodeId.ToString();
                            }

                            results.AddRange(childReferences);
                        }
                        catch (Exception)
                        {
                            // skip this node
                            continue;
                        }

                        processedReferences.Add(nodeReference.NodeId.ToString());
                        results.Add(nodeInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Cannot browse node {0}: {1}", nodeId, ex.Message);

                throw;
            }

            return results;
        }
    }
}
