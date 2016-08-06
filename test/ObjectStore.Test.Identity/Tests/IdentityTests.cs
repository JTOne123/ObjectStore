﻿using Xunit;
using System;
using System.Linq;
using Xunit.Abstractions;
using System.Threading.Tasks;
using ObjectStore.Test.Identity.Fixtures;
using Microsoft.AspNetCore.Identity;
using ObjectStore.Identity;
using ObjectStore.Interfaces;

namespace ObjectStore.Test.Identity
{
    public class IdentityTests : IClassFixture<TestServerFixture>
    {
        #region Subclasses
        class UserMock : User
        {
            public override int Id => 0;

            public override string Name { get; set; }

            public override string NormalizedUsername { get; set; }

            public override string Password { get; set; }
        }
        #endregion

        #region Fields
        ITestOutputHelper _output;
        TestServerFixture _fixture;
        #endregion

        #region Constructor
        public IdentityTests(ITestOutputHelper output, TestServerFixture fixture)
        {
            _output = output;
            _fixture = fixture;
        }
        #endregion

        #region Tests
        [Fact]
        public async Task TestSignInSuccess()
        {
            SignInResult result = await _fixture.Execute((SignInManager<User> signInManager) => signInManager.PasswordSignInAsync("Admin", "test", false, false));

            Assert.NotNull(result);
            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task TestSignInFail()
        {
            SignInResult result = await _fixture.Execute((SignInManager<User> signInManager) => signInManager.PasswordSignInAsync("Admin", "1234", false, false));

            Assert.NotNull(result);
            Assert.False(result.Succeeded);
        }

        [Fact()]
        public async Task TestRegister()
        {
            string userName = $"Test{(DateTime.Now - new DateTime(2016, 1, 1)).TotalHours}";

            IdentityResult identityResult = await _fixture.Execute(async (UserManager<User> userManager) => {
                UserMock mock = new UserMock() { Name = userName };
                return await userManager.CreateAsync(mock, "Passw0rd!");
            });

            Assert.True(identityResult.Succeeded);

            ObjectStoreManager.DefaultObjectStore.GetQueryable<User>().DropChanges();
            await ObjectStoreManager.DefaultObjectStore.GetQueryable<User>().FetchAsync();

            SignInResult signInResult = await _fixture.Execute((SignInManager<User> signInManager) => signInManager.PasswordSignInAsync(userName, "Passw0rd!", false, false));

            Assert.True(signInResult.Succeeded);
        }

        [Fact()]
        public async Task TestChangePassword()
        {
            IdentityResult identityResult = await _fixture.Execute(async (UserManager<User> userManager, IObjectProvider objectProvider) => {
                User user = objectProvider.GetQueryable<User>().Where(x => x.Name == "User1").FirstOrDefault();
                return await userManager.ChangePasswordAsync(user, "Passw0rd!", "testPassword1!");
            });

            Assert.True(identityResult.Succeeded);

            ObjectStoreManager.DefaultObjectStore.GetQueryable<User>().DropChanges();
            await ObjectStoreManager.DefaultObjectStore.GetQueryable<User>().FetchAsync();

            SignInResult signInResult = await _fixture.Execute((SignInManager<User> signInManager) => signInManager.PasswordSignInAsync("User1", "testPassword1!", false, false));

            Assert.True(signInResult.Succeeded);
        }
        #endregion
    }
}