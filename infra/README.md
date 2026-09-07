# GrandNode2 on Azure — Terraform

```
infra/
├── modules/grandnode/     the reusable module - all resources live here
└── environments/
    ├── prod/              one root per environment: provider, backend, state
    └── staging/
```

## Why the split

The module declares `required_providers` but **no `provider` block** and no
`subscription_id` — provider configuration belongs to the calling root. That is
what makes it reusable: each environment configures its own subscription and its
own state, and both call identical resource code.

Adding an environment is a `main.tf` of about twenty lines. Fixing a bug in the
module fixes it everywhere, instead of in whichever copy you remembered.

## Usage

```bash
cd environments/prod        # or staging
terraform init
terraform plan
terraform apply
```

Each environment keeps a separate state file, so `apply` in staging can never
touch production.

## Publishing the module separately

Move `modules/grandnode/` to its own repository, tag it, then pin by version:

```hcl
module "grandnode" {
  source = "github.com/<you>/terraform-azurerm-grandnode?ref=v1.0.0"
  ...
}
```

Environments then upgrade deliberately — staging to `v1.1.0` first, production
when you are satisfied — instead of every environment moving together.

## Deployment order

The module's `next_steps` output is the runbook. In short:

1. `terraform apply` — the container app is created before its image exists, so
   its first revision fails to pull. Expected.
2. `az acr build -r <registry> -t grandnode:<tag> -f Dockerfile .` from the
   repository root, then
   `terraform apply -replace=module.grandnode.azurerm_container_app.this`.
3. Install with `enable_installer = true` **and `min_replicas = 1`** — the
   `/install` POST seeds the database inside a single HTTP request and times out
   on a cold start. A `check` block enforces this pairing.
4. Set Collation to `-None-` on the form. It does not default to it, and Cosmos
   vCore rejects every other value.
5. **Restart the revision.** `InstallController` calls `ResetCache()`, which
   latches the app into reporting itself unhealthy for the life of the process.
6. Set `enable_installer = false` and scale back down.

Volumes are optional and staged separately — see `modules/grandnode/README.md`.

## Notes

- `terraform.tfvars` in each environment holds only `subscription_id`; every
  other input is explicit in `main.tf` so the configuration is reviewable.
- `.terraform.lock.hcl` should be committed. State files should not.
