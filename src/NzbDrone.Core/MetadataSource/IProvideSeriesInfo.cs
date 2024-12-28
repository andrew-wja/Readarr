using NzbDrone.Core.MetadataSource.BookInfo;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideSeriesInfo
    {
        SeriesResource GetSeriesInfo(int id, bool useCache = true);
    }
}
