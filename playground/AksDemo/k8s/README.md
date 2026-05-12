# cert-manager + AGC HTTP-01 TLS (manual step)

The AppHost installs cert-manager into the cluster via `aks.AddHelmChart(...)`,
but the ClusterIssuer and the TLS listener on `storefront-gw` are not yet
expressible in the Aspire app model — this folder holds the manifests you apply
by hand after `aspire deploy`.

## What `aspire deploy` already does

- Installs cert-manager `v1.18.2` from `oci://quay.io/jetstack/charts/cert-manager`.
- Enables the Gateway API integration (`config.enableGatewayAPI=true`), which is
  what lets cert-manager react to Gateway listeners with TLS configuration.
- Creates the `storefront-gw` Gateway with a port 80 HTTP listener — required
  for HTTP-01 challenges to succeed.

## Post-deploy steps

1. Point a DNS record at the storefront frontend FQDN. The FQDN shows up on the
   Gateway once AGC programs it:

   ```bash
   kubectl get gateway storefront-gw \
     -o jsonpath='{.status.addresses[0].value}'
   ```

   Then create a CNAME like `storefront.example.com -> <fqdn>.alb.azure.com`.

2. Edit `cluster-issuer.yaml` and replace `REPLACE_ME@example.com` with a real
   contact email. Apply both issuers:

   ```bash
   kubectl apply -f cluster-issuer.yaml
   ```

3. Edit `gateway-tls.patch.yaml` and replace `REPLACE_ME.example.com` with the
   hostname you just created. Patch the gateway:

   ```bash
   kubectl patch gateway storefront-gw \
     --type=merge \
     --patch-file=gateway-tls.patch.yaml
   ```

4. Watch cert-manager issue the certificate. The Certificate resource is
   auto-created by cert-manager's Gateway API integration once the patched
   Gateway is observed:

   ```bash
   kubectl get certificate,certificaterequest,order,challenge -A -w
   ```

   First time around it usually takes 1–3 minutes. The `Challenge` resource
   creates a transient HTTPRoute on `storefront-gw` so the ACME server can hit
   `http://<hostname>/.well-known/acme-challenge/<token>`.

5. Verify TLS:

   ```bash
   curl -v https://storefront.example.com/api
   ```

   Initially the cert will be from Let's Encrypt **staging**, so `curl` will
   complain about the issuer — pass `--insecure` or import the staging root to
   trust it. Once everything works, switch the annotation in
   `gateway-tls.patch.yaml` from `letsencrypt-staging` to `letsencrypt-prod`,
   reapply the patch, and delete the staging Certificate + Secret so a fresh
   prod one is issued:

   ```bash
   kubectl patch gateway storefront-gw --type=merge --patch-file=gateway-tls.patch.yaml
   kubectl delete certificate storefront-tls
   kubectl delete secret storefront-tls
   ```

## Cleanup

`aspire destroy` will uninstall cert-manager (`WithDestroy()` is set in the
AppHost). The ClusterIssuer + Certificate + Secret resources do not survive
that uninstall because cert-manager deletes its CRDs. If you only ran
`aspire deploy` against a single environment, the cluster itself comes down
with the rest of the Bicep destroy.

## Why this isn't an Aspire API yet

The next iteration would be something like:

```csharp
aks.AddCertManager()
   .AddAcmeIssuer("letsencrypt", email: "...", server: AcmeServer.LetsEncryptStaging);

aks.AddGateway("storefront-gw")
   .WithTls("storefront.example.com", issuer: "letsencrypt");
```

…but we want to validate the underlying Helm + manifest flow on a real cluster
first before locking the resource model down.
