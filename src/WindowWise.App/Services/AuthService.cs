using System;
using System.Collections.Generic;
using System.Text;

namespace WindowWise.Services
{
    /// <summary>
    /// authentication service for user sign in and sign out, and get access token for the user
    /// </summary>

    public interface AuthService
    {
        Task SignInAsync(CancellationToken cancellationToken);
        Task <string> GetAccessToeknAsync(CancellationToken cancellationToken);

        Task SignOutAsync(CancellationToken cancellationToken);


    }
}
