using NzbDrone.Core.MetadataSource.Goodreads;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideGoodreadsSeriesInfo
    {
        SeriesResource GetGoodreadsSeriesInfo(int id, bool useCache = true);
    }
}
