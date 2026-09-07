namespace Grand.Infrastructure.Configuration;

public class AzureConfig
{
    /// <summary>
    ///     Connection string for Azure BLOB storage
    /// </summary>
    public string AzureBlobStorageConnectionString { get; set; }

    /// <summary>
    ///     Container name for Azure BLOB storage
    /// </summary>
    public string AzureBlobStorageContainerName { get; set; }

    /// <summary>
    ///     End point for Azure BLOB storage
    /// </summary>
    public string AzureBlobStorageEndPoint { get; set; }


    /// <summary>
    ///     Connection string for PersistKeys Azure BLOB storage
    /// </summary>
    public string PersistKeysAzureBlobStorageConnectionString { get; set; }

    /// <summary>
    ///     Indicates whether we should use Azure Key Vault to store data protection keys
    /// </summary>
    public bool PersistKeysToAzureKeyVault { get; set; }

    /// <summary>
    ///     Indicates whether we should use Azure blob storage to store data protection
    /// </summary>
    public bool PersistKeysToAzureBlobStorage { get; set; }

    /// <summary>
    ///     Azure blob storage container name
    /// </summary>
    public string DataProtectionContainerName { get; set; }

    /// <summary>
    ///     Azure blob storage blob name
    /// </summary>
    public string DataProtectionBlobName { get; set; }

    /// <summary>
    ///     The keyIdentifier is the key vault key identifier used for key encryption.
    /// </summary>
    public string KeyIdentifier { get; set; }

    /// <summary>
    ///     Container holding the raw (bronze) layer of the data lake. Empty disables lake writing
    ///     entirely - the application keeps working, it simply publishes nothing.
    /// </summary>
    public string LakeBronzeContainerName { get; set; }

    /// <summary>
    ///     Connection string for the lake account. Falls back to AzureBlobStorageConnectionString
    ///     when empty, so a single-account deployment needs no extra configuration.
    /// </summary>
    public string LakeStorageConnectionString { get; set; }
}