namespace GraphQLTemplate.Repositories
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using GraphQLTemplate.Models;

    public interface IHumanRepository
    {
#if Subscriptions
        IObservable<Human> WhenHumanCreated { get; }

#endif
        Task<Human> AddHumanAsync(HumanInput humanInput, CancellationToken cancellationToken);

        Task<List<Character>> GetFriendsAsync(Human human, CancellationToken cancellationToken);

        Task<IQueryable<Human>> GetHumansAsync(CancellationToken cancellationToken);

        Task<IEnumerable<Human>> GetHumansAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    }
}
