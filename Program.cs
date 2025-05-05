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
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

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

            // Acquire an access token using MSAL (Client Credentials Flow)

            // Load configuration from appsettings.json
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            Configuration = builder.Build();

            await InitiliaseSignalR();

            await RecognizeKeywordAsync();

            // Keep the program running in a loop
            while (true)
            {
                try
                {
                    // Start keyword recognition
                    //await RecognizeKeywordAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in main loop: {ex.Message}");
                }
            }

        }

        private static async Task InitiliaseSignalR()
        {

            using PwmChannel pwmChannel1 = PwmChannel.Create(0, 0, 50);
            using ServoMotor servoMotor1 = new ServoMotor(pwmChannel1, 180, 700, 2400);

            using PwmChannel pwmChannel2 = PwmChannel.Create(0, 1, 50);
            using ServoMotor servoMotor2 = new ServoMotor(pwmChannel2, 180, 700, 2400);

            using SoftwarePwmChannel pwmChannel3 = new SoftwarePwmChannel(27, 50, 0.5, true);
            using ServoMotor servoMotor3 = new ServoMotor(pwmChannel3, 180, 900, 2100);

            servoMotor1.Start();
            servoMotor2.Start();
            servoMotor3.Start();

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
                    // Speak the received message
                    await SpeakMessageAsync(message);

                    await RecognizeKeywordAsync();
                });

                hubConnection.On<string, string, string, bool, List<CognitiveSearchResult>>("ReceiveMessageToken", async (chatBubbleId, user, messageToken, isTemporaryResponse, sources) =>
                {

                    /*

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

                    */

                });

                await hubConnection.StartAsync();
                //Console.WriteLine("Query: What are you?");
                //await hubConnection.SendAsync("SendQuery", "What are you?", chatMessages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private static async Task RecognizeKeywordAsync()
        {
            var speechKey = Configuration["AzureSpeech:Key"];
            var serviceRegion = Configuration["AzureSpeech:Region"];
            var keywordModelPath = Configuration["AzureSpeech:KeywordModelPath"]; // Path to keyword model

            var config = SpeechConfig.FromSubscription(speechKey, serviceRegion);
            using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
            using var keywordRecognizer = new KeywordRecognizer(audioConfig);

            var keywordModel = KeywordRecognitionModel.FromFile(keywordModelPath);

            Console.WriteLine("Listening for the keyword...");

            var result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);

            if (result.Reason == ResultReason.RecognizedKeyword)
            {
                Console.WriteLine($"Recognized keyword: {result.Text}");
                await RespondToKeywordAsync();
            }
            else
            {
                Console.WriteLine("Keyword not recognized. Listening again...");
                await RecognizeKeywordAsync();
            }
        }

        private static async Task RespondToKeywordAsync()
        {
            try
            {
                var speechKey = Configuration["AzureSpeech:Key"];
                var serviceRegion = Configuration["AzureSpeech:Region"];

                var config = SpeechConfig.FromSubscription(speechKey, serviceRegion);
                config.SpeechSynthesisVoiceName = "en-US-GuyNeural";

                using var synthesizer = new SpeechSynthesizer(config);
                var responseText = "Ok, what would you like to chat about?";

                Console.WriteLine(responseText);

                await synthesizer.SpeakTextAsync(responseText);

                await RecognizeSpeechAsync();

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

        }

        private static async Task RecognizeSpeechAsync()
        {
            try
            {
                var speechKey = Configuration["AzureSpeech:Key"];
                var serviceRegion = Configuration["AzureSpeech:Region"];

                var config = SpeechConfig.FromSubscription(speechKey, serviceRegion);
                using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
                using var recognizer = new SpeechRecognizer(config, audioConfig);

                Console.WriteLine("Listening for user input...");
                var result = await recognizer.RecognizeOnceAsync();

                if (result.Reason == ResultReason.RecognizedSpeech)
                {
                    Console.WriteLine($"Recognized: {result.Text}");
                    await hubConnection.SendAsync("SendQuery", result.Text, chatMessages);
                }
                else
                {
                    Console.WriteLine("Speech not recognized. Listening again...");
                    await RecognizeSpeechAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        // Add this method to handle speech synthesis
        private static async Task SpeakMessageAsync(string message)
        {
            try
            {
                var speechKey = Configuration["AzureSpeech:Key"];
                var serviceRegion = Configuration["AzureSpeech:Region"];

                var config = SpeechConfig.FromSubscription(speechKey, serviceRegion);
                config.SpeechSynthesisVoiceName = "en-US-GuyNeural"; // You can change the voice as needed

                using var synthesizer = new SpeechSynthesizer(config);
                await synthesizer.SpeakTextAsync(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        static void MoveToAngle(ServoMotor Servo, int Angle)
        {
            Servo.WriteAngle(Angle);
        }
    }
}