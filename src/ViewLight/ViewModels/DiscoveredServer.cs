using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;

namespace ViewLight.ViewModels;

/// <summary>
/// One server found on the local network, with the command to join it.
/// </summary>
/// <remarks>
/// The command lives on the row rather than on the window's view model because the row template needs a
/// command that already knows its own address; binding back up to the parent for one would be more
/// machinery for less clarity.
/// </remarks>
public sealed class DiscoveredServer {

    public DiscoveredServer(ServerEndpoint endpoint, Func<ServerEndpoint, Task> join) {
        Endpoint = endpoint;
        JoinCommand = new AsyncRelayCommand(() => join(endpoint));
    }

    public ServerEndpoint Endpoint { get; }

    public string Address => Endpoint.ToString();

    public ICommand JoinCommand { get; }
}
