using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sportarr.Api.Tests.Services;

public class EnhancedDownloadMonitorServiceTests
{
    private readonly ILogger<EnhancedDownloadMonitorService> _logger;
    private readonly Mock<IDownloadClientService> _mockDownloadClientService;
    private readonly Mock<IFileImportService> _mockFileImportService;
    private readonly Mock<IConfigService> _mockConfigService;
    private readonly IServiceCollection _services;

    public EnhancedDownloadMonitorServiceTests()
    {
        _logger = Mock.Of<ILogger<EnhancedDownloadMonitorService>>();
        _mockDownloadClientService = new Mock<IDownloadClientService>();
        _mockFileImportService = new Mock<IFileImportService>();
        _mockConfigService = new Mock<IConfigService>();
        _services = new ServiceCollection();
        _services.AddScoped(_ => _mockDownloadClientService.Object);
        _services.AddScoped(_ => _mockFileImportService.Object);
        _services.AddSingleton(_ => _mockConfigService.Object);
    }

    public async Task HandleDownloadWithoutStatus_ShouldRemoveDownloadFromQueueAfterThreeFailedAttemps()
    {

    }
}
