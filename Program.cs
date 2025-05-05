﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Device.Pwm;
using System.Device.Pwm.Drivers;
using Iot.Device.ServoMotor;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http;
using Microsoft.Identity.Client;
using clippydotnet.Shared;
using Microsoft.Extensions.Configuration;

namespace clippydotnet
{
    class Program
    {
        private static HubConnection hubConnection;
        private static List<OpenAIChatMessage> chatMessages = new List<OpenAIChatMessage>();
        private static IConfiguration Configuration;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Hello World!");

            using PwmChannel pwmChannel1 = PwmChannel.Create(0, 0, 50);
            using ServoMotor servoMotor1 = new ServoMotor(pwmChannel1, 180, 700, 2400);

            using PwmChannel pwmChannel2 = PwmChannel.Create(0, 1, 50);
            using ServoMotor servoMotor2 = new ServoMotor(pwmChannel2, 180, 700, 2400);

            using SoftwarePwmChannel pwmChannel3 = new SoftwarePwmChannel(27, 50, 0.5, true);
            using ServoMotor servoMotor3 = new ServoMotor(pwmChannel3, 180, 900, 2100);

            servoMotor1.Start();
            servoMotor2.Start();
            servoMotor3.Start();

            // Acquire an access token using MSAL (Client Credentials Flow)

            // Load configuration from appsettings.json
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            Configuration = builder.Build();

            // Retrieve Azure AD settings
            var clientId = Configuration["AzureAd:ClientId"];
            var clientSecret = Configuration["AzureAd:ClientSecret"];
            var tenantId = Configuration["AzureAd:TenantId"];
            var appIdUri = Configuration["AzureAd:AppIdUri"];
            
            // Use the settings in your MSAL configuration
            var clientApp = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            var scopes = new[] { $"{appIdUri}/.default" };
            
            AuthenticationResult authResult = null;

            try
            {
                authResult = await clientApp.AcquireTokenForClient(scopes).ExecuteAsync();
                //Console.WriteLine($"Access token: {authResult.AccessToken}");
            }
            catch (MsalServiceException ex)
            {
                Console.WriteLine($"Error acquiring token: {ex.Message}");
            }

            // Test access to the web app
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authResult.AccessToken);

                try
                {
                    var response = await httpClient.GetAsync("https://pjgopenaiwebapp.azurewebsites.net/");
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Successfully accessed the web app.");
                    }
                    else
                    {
                        Console.WriteLine($"Failed to access the web app. Status code: {response.StatusCode}");
                        Console.WriteLine($"Response: {await response.Content.ReadAsStringAsync()}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accessing the web app: {ex.Message}");
                }
            }

            hubConnection = new HubConnectionBuilder()
                .WithUrl("https://pjgopenaiwebapp.azurewebsites.net/chathub", options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(authResult.AccessToken);
                })
                .WithAutomaticReconnect()
                .Build();

            Console.CancelKeyPress += async (sender, e) =>
            {
                Console.WriteLine("Disconnecting...");
                servoMotor1.Stop();
                servoMotor2.Stop();
                servoMotor3.Stop();
                await hubConnection.DisposeAsync();
                Environment.Exit(0);
            };

            try
            {
                hubConnection.On<string, string, string>("ReceiveMessage", async (responseGuid, user, message) =>
                {
                    //Console.WriteLine($"Received message: {message} from {user} with guid {responseGuid}");
                });

                hubConnection.On<string, string, string, bool, List<CognitiveSearchResult>>("ReceiveMessageToken", async (chatBubbleId, user, messageToken, isTemporaryResponse, sources) =>
                {
                    // Find the chat message with the supplied chatBubbleId
                    var chatMessage = chatMessages.Where(chatMessageItem => chatMessageItem.ChatBubbleId == chatBubbleId).FirstOrDefault();

                    if (chatMessage != null)
                    {
                        if (chatMessage.IsTemporaryResponse)
                        {
                            chatMessage.Content = "";
                            chatMessage.IsTemporaryResponse = false;
                        }

                        chatMessage.Content = chatMessage.Content + messageToken;
                    }
                    else
                    {
                        chatMessages.Add(new OpenAIChatMessage { ChatBubbleId = chatBubbleId, Content = messageToken, Type = "ai", IsTemporaryResponse = isTemporaryResponse, Sources = sources });
                    }

                    // Stream the reponse messageToken to the console
                    Console.Write(messageToken);
                });

                await hubConnection.StartAsync();
                Console.WriteLine("Query: What are you?");
                await hubConnection.SendAsync("SendQuery", "What are you?", chatMessages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            while (true)
            {
                // Keep the application running
            }
        }

        static void MoveToAngle(ServoMotor Servo, int Angle)
        {
            Servo.WriteAngle(Angle);
        }
    }
}