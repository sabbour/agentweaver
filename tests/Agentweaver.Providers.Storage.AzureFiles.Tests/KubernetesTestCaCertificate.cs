using System.Text;

namespace Agentweaver.Providers.Storage.AzureFiles.Tests;

/// <summary>Provides public certificates shared by the C# and Node.js pin test vectors.</summary>
internal static class KubernetesTestCaCertificate
{
    public static readonly byte[] Primary = Encoding.UTF8.GetBytes(
        """
        -----BEGIN CERTIFICATE-----
        MIIC4TCCAcmgAwIBAgIIb1toLOQ3IMAwDQYJKoZIhvcNAQELBQAwJjEkMCIGA1UE
        AxMbQWdlbnR3ZWF2ZXIgVGVzdCBDbHVzdGVyIENBMB4XDTI2MTAxODA4MjY1OFoX
        DTI4MTAxOTA4MjY1OFowJjEkMCIGA1UEAxMbQWdlbnR3ZWF2ZXIgVGVzdCBDbHVz
        dGVyIENBMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAyHoYYKlm3k14
        eaqb+/NP9v6I7kB4KoxWe3YYY3BXU51zgX4vFDduds6gJ73B+vS0rkb8nJcn+2Je
        326D4MVuMyMKlEMGZxZYKeVJLChQo+D264OFoiiF5G+xxsIjSm86abSXdZ/2Wejs
        YwCpt/Lch1ltg5mDK9ODDztFX0xjMl65CiE973b3lKxBD21HTKKph52mkR6TnwQF
        4Rlp3jkWna/GoLThwfLTTRHD4cJpMJsqHD8N9LX+CVPLNWkVprvZY4ElBYghEjux
        ipIcVSh+idUmzQtdd8ublhndgEtbqvUPLeWrzKnhzjyf1chCfexz1xLM4wa8cXv/
        NZzE4tuuxQIDAQABoxMwETAPBgNVHRMBAf8EBTADAQH/MA0GCSqGSIb3DQEBCwUA
        A4IBAQAcCouj+1QEIUvUtoNuh2W6ntbyuCgvlP19L8mQJqhsCsLPrxo0MFZwmkmO
        TbmrMv/+MHjIhLDVraZtuDgkbQYQaw8jzNIr2S2XvbatL5Lbx1fxvRw4xXof7VCr
        +FZZL6iaC+D1H8rrNWpxuoe1nla9PjiWgTLdrZFX6WLfNX9S/AE7xL/1rMyJiTRu
        qDZL4HJSAIqld8p99VvNp0kftjITGxk/OX5eQ7WPQcO19HMVwN6MdsddON5v/bex
        9uMQpAg2GlRwQzU4ipIObfqOkvGlSRfxw2LIwK7MtiWOqryms7FCNMpr5YKJ6ANX
        iS+KicuYWF/IR15Vdq0FWBp8daa+
        -----END CERTIFICATE-----
        """);

    public static readonly byte[] PrimaryWithComment = Encoding.UTF8.GetBytes(
        $"# Cluster root CA\n{Encoding.UTF8.GetString(Primary)}");

    public static readonly byte[] Other = Encoding.UTF8.GetBytes(
        """
        -----BEGIN CERTIFICATE-----
        MIIC7TCCAdWgAwIBAgIIPok6wh4DMbAwDQYJKoZIhvcNAQELBQAwLDEqMCgGA1UE
        AxMhQWdlbnR3ZWF2ZXIgT3RoZXIgVGVzdCBDbHVzdGVyIENBMB4XDTI2MTAxODA4
        MjcwNloXDTI4MTAxOTA4MjcwNlowLDEqMCgGA1UEAxMhQWdlbnR3ZWF2ZXIgT3Ro
        ZXIgVGVzdCBDbHVzdGVyIENBMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKC
        AQEAvjgCBvAWUQwh+sKE4rTrXgMPFqrTjz9OIPKFjOAhpXpK1WeqiLB+jbIFqc33
        hNaRcvkiKhI8XpABkywaNwsSajXxSG3uMakcliUjbIHkWmFZEfDvSP0Zt+kW9Fp4
        77Nip3MUZVDcNSc0NLK6fxhIRq/W3e50RxpLtNhprkk/moh+OOEEJVdtXDHQ2A8c
        0jg6V3e9SmMwezU7ANdVAbN1vunKgd+Dfz3Ovm4MPjPizHh9Wr0/h60M8H+PzQLC
        0mKzVY6+kcD6GElIulDY9zjHrdpTfkqL0KhYgwY1gN1uyyjRA9KIqRvY0Jw6cH+t
        CCoInIcx2xr2smVrsaitAV3CYQIDAQABoxMwETAPBgNVHRMBAf8EBTADAQH/MA0G
        CSqGSIb3DQEBCwUAA4IBAQBAzabzwpmpCqY6eFfZAtot1s6OvD+cYAVTtBb/lEVi
        6K00GThMhZc/NIVDkblpsuVUTMcNEQflRwGltsmKNrmQ62gcPGEXUUIgyPWGeWIv
        dDSDCO0Fer7auZTmpf2ceo3UZD42Q3CQes5k4RuAQyIXc7xWkqAYbgtPi/hOrz1v
        0w+TTsjgre0280z6oWo1hkMLoq1xPBM3rWLoJtpruMh/Btvkz7EyZPBLjgAWNMdB
        uCAvoNDsRg/CDYGpwUkwB0c7rhT9rSFK8xtalXElG7FowJZRsXQwYaqgxwBf6DXN
        86sgYyQWtINDGfh5pjJ2FeFetmaMwNZ2L6s1Ve3uSUTl
        -----END CERTIFICATE-----
        """);
}
