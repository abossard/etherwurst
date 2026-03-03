namespace SpotPriceDashboard.Data;

public static class RegionFormatter
{
    public static string Format(string region) => region switch
    {
        "eastus" => "🇺🇸 East US",
        "eastus2" => "🇺🇸 East US 2",
        "westus2" => "🇺🇸 West US 2",
        "westus3" => "🇺🇸 West US 3",
        "centralus" => "🇺🇸 Central US",
        "northeurope" => "🇮🇪 North Europe",
        "westeurope" => "🇳🇱 West Europe",
        "uksouth" => "🇬🇧 UK South",
        "swedencentral" => "🇸🇪 Sweden Central",
        "germanywestcentral" => "🇩🇪 Germany West Central",
        "francecentral" => "🇫🇷 France Central",
        "switzerlandnorth" => "🇨🇭 Switzerland North",
        "southeastasia" => "🇸🇬 Southeast Asia",
        "eastasia" => "🇭🇰 East Asia",
        "japaneast" => "🇯🇵 Japan East",
        "australiaeast" => "🇦🇺 Australia East",
        "canadacentral" => "🇨🇦 Canada Central",
        "brazilsouth" => "🇧🇷 Brazil South",
        "centralindia" => "🇮🇳 Central India",
        "koreacentral" => "🇰🇷 Korea Central",
        _ => region
    };
}
