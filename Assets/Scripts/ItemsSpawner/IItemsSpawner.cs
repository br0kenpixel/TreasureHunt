using System.Collections.Generic;

public interface IItemSpawner
{
    public List<Item> SpawnItems(int count);
}