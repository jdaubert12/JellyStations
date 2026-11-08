using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Reads play state through Jellyfin's user data manager. Nothing is written.</summary>
public sealed class JellyfinHouseholdHistory : IHouseholdHistory
{
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILibraryManager _library;

    public JellyfinHouseholdHistory(IUserManager users, IUserDataManager userData, ILibraryManager library)
    {
        _users = users;
        _userData = userData;
        _library = library;
    }

    public IReadOnlyList<HouseholdUser> Users()
        => _users.GetUsers()
            .Select(u => new HouseholdUser(u.Id, u.Username))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyDictionary<Guid, IReadOnlyList<WatchState>> Read(IEnumerable<Guid> itemIds, IReadOnlyList<Guid> userIds)
    {
        var users = userIds.Distinct().Select(_users.GetUserById).Where(u => u is not null).Select(u => u!).ToList();
        var result = new Dictionary<Guid, IReadOnlyList<WatchState>>();
        if (users.Count == 0)
        {
            return result;
        }

        foreach (var id in itemIds.Distinct())
        {
            var item = _library.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            var states = new List<WatchState>(users.Count);
            foreach (var user in users)
            {
                var data = _userData.GetUserData(user, item);
                var at = data?.LastPlayedDate;
                states.Add(new WatchState(data?.Played == true, at.HasValue ? DateTime.SpecifyKind(at.Value.ToUniversalTime(), DateTimeKind.Utc) : null));
            }

            result[id] = states;
        }

        return result;
    }
}
