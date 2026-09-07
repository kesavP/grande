terraform {
  required_version = ">= 1.9.0"

  # A child module declares which providers it needs, but must NOT configure
  # them - provider blocks belong to the calling root module. See environments/.
  required_providers {
    azurerm = {
      source = "hashicorp/azurerm"
      # azurerm_mongo_cluster (Cosmos DB for MongoDB vCore) requires 4.x.
      version = "~> 4.20"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}
