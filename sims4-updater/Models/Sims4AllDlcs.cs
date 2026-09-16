using sims4_updater.Helpers;
using System.Collections.ObjectModel;

namespace sims4_updater.Models
{
    public class Sims4AllDlcs
    {
        public ObservableCollection<Sims4DLC> Sims4DLCs { get; set; } = new ObservableCollection<Sims4DLC>();
        public ObservableCollection<Sims4DLC> OriginalSims4DLCs { get; set; } = new ObservableCollection<Sims4DLC>();

        public Sims4AllDlcs()
        {
            //Rozszerzenia (EP)
            Sims4DLCs.Add(new Sims4DLC { Code = "EP01", Name = "The Sims 4 Get to Work", Url = "https://file-eu-par-2.gofile.io/download/web/f1ada541-43cd-4119-8350-0e74741d2fc8/Sims4_DLC_EP01_Get_to_Work.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP02", Name = "The Sims 4 Get Together", Url = "https://file-na-phx-1.gofile.io/download/web/88bf0cc1-47a1-4a65-8ece-cbeae74de025/Sims4_DLC_EP02_Get_Together.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP03", Name = "The Sims 4 City Living", Url = "https://file-eu-par-1.gofile.io/download/web/fdd17128-c91e-468f-b60d-16cd55dbdffd/Sims4_DLC_EP03_City_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP04", Name = "The Sims 4 Cats & Dogs", Url = "https://file-eu-ldn-1.gofile.io/download/web/ffffec45-4b4f-482a-a5e6-0bb8204f6c31/Sims4_DLC_EP04_Cats_and_Dogs.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP05", Name = "The Sims 4 Seasons", Url = "https://file-eu-par-1.gofile.io/download/web/eb85e3ef-40d6-4caa-a737-80066c531df6/Sims4_DLC_EP05_Seasons.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP06", Name = "The Sims 4 Get Famous", Url = "https://file-na-phx-1.gofile.io/download/web/f5412330-454c-40b9-ac92-e3a410f2ec5d/Sims4_DLC_EP06_Get_Famous.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP07", Name = "The Sims 4 Island Living", Url = "https://file-eu-ldn-1.gofile.io/download/web/e4ac3d4f-4d83-4d87-bee7-e609311e6f66/Sims4_DLC_EP07_Island_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP08", Name = "The Sims 4 Discover University", Url = "https://file-na-phx-1.gofile.io/download/web/d420a53d-dd42-42a0-88aa-015e122081f6/Sims4_DLC_EP08_Discover_University.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP09", Name = "The Sims 4 Eco Lifestyle", Url = "https://file-na-phx-1.gofile.io/download/web/bc0a712e-1bbd-4af3-8b8f-f183d7ac2cc8/Sims4_DLC_EP09_Eco_Lifestyle.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP10", Name = "The Sims 4 Snowy Escape", Url = "https://file-na-phx-1.gofile.io/download/web/f7574ab6-4367-451b-adcd-6e151f0bfa92/Sims4_DLC_EP10_Snowy_Escape.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP11", Name = "The Sims 4 Cottage Living", Url = "https://file-na-phx-1.gofile.io/download/web/c4341fc6-7268-4f82-8c81-489f5a46bbfb/Sims4_DLC_EP11_Cottage_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP12", Name = "The Sims 4 High School Years", Url = "https://file-na-phx-1.gofile.io/download/web/aeea599e-8519-417b-b272-72627d3ef4aa/Sims4_DLC_EP12_High_School_Years.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP13", Name = "The Sims 4 Growing Together", Url = "https://file-eu-par-5.gofile.io/download/web/cda52dec-f0c8-4694-8014-4852666e624d/Sims4_DLC_EP13_Growing_Together.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP14", Name = "The Sims 4 Horse Ranch", Url = "https://file-eu-par-1.gofile.io/download/web/c7116b73-f4ae-4dc2-9a20-ec14452fe89b/Sims4_DLC_EP14_Horse_Ranch.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP15", Name = "The Sims 4 For Rent", Url = "https://file-eu-ldn-1.gofile.io/download/web/edb3fa92-aff0-4b39-8763-aa04a63c9d4d/Sims4_DLC_EP15_For_Rent.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP16", Name = "The Sims 4 Lovestruck", Url = "https://file-na-phx-1.gofile.io/download/web/dc6398c7-6c6b-42f4-916c-b69f5f57c6fc/Sims4_DLC_EP16_Lovestruck.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP17", Name = "The Sims 4 Life & Death", Url = "https://file-na-phx-1.gofile.io/download/web/954f0789-cdc5-4f7e-8e0f-bbca04fa8d6b/Sims4_DLC_EP17_Life_and_Death.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP18", Name = "The Sims 4 Businesses & Hobbies", Url = "https://file-na-phx-1.gofile.io/download/web/96cf01c2-4b10-467e-9bbd-149894be31ee/Sims4_DLC_EP18_Businesses_and_Hobbies.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP19", Name = "The Sims 4 Enchanted by Natures", Url = "https://file-eu-par-5.gofile.io/download/web/093eb888-23ff-4b9a-a177-ad0be88ab931/Sims4_DLC_EP19_Enchanted_by_Nature_Expansion_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP20", Name = "The Sims 4 Adventure Awaits", Url = "https://file-na-phx-1.gofile.io/download/web/a0818471-ab3d-4fa6-9afc-54404913d693/Sims4_DLC_EP20_Adventure_Awaits_Expansion_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP21", Name = "The Sims 4 Royalty & Legacy", Url = "https://file-eu-par-5.gofile.io/download/web/ccca342c-4b57-476e-8ad6-1d1907f57c02/EP21%200.zip", Installed = false });
            //FP
            Sims4DLCs.Add(new Sims4DLC { Code = "FP01", Name = "The Sims 4 Holiday Celebration", Url = "https://file-eu-par-2.gofile.io/download/web/88693d77-bf4b-4dd8-9701-913815c4630d/Sims4_DLC_FP01_Holiday_Celebration_Pack.zip", Installed = false});
            //Pakiety (GP)
            Sims4DLCs.Add(new Sims4DLC { Code = "GP01", Name = "The Sims 4 Outdoor Retreat", Url = "https://file-eu-par-5.gofile.io/download/web/a0415412-8b9d-40ea-9299-f4cb1d961fbf/Sims4_DLC_GP01_Outdoor_Retreat.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP02", Name = "The Sims 4 Spa Day", Url = "https://file-na-phx-1.gofile.io/download/web/b1fdbe86-54d3-4cd3-8bee-ab7eac2065c9/Sims4_DLC_GP02_Spa_Day.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP03", Name = "The Sims 4 Dine Out", Url = "https://file-na-phx-1.gofile.io/download/web/98bd1f05-79da-46e5-8c56-b377cb5446b0/Sims4_DLC_GP03_Dine_Out.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP04", Name = "The Sims 4 Vampires", Url = "https://file-na-phx-1.gofile.io/download/web/ce353006-b48d-49b5-86ea-3e4a224fb3ec/Sims4_DLC_GP04_Vampires.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP05", Name = "The Sims 4 Parenthood", Url = "https://file-eu-par-5.gofile.io/download/web/bdc70cab-eac3-4667-9aa8-69f86291a725/Sims4_DLC_GP05_Parenthood.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP06", Name = "The Sims 4 Jungle Adventure", Url = "https://file-eu-par-2.gofile.io/download/web/b32c4b31-8356-4def-9bad-495de297ac3b/Sims4_DLC_GP06_Jungle_Adventure.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP07", Name = "The Sims 4 StrangerVille", Url = "https://file-na-phx-1.gofile.io/download/web/2bee9d3d-efda-4628-8d3d-0935479f2db7/Sims4_DLC_GP07_StrangerVille.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP08", Name = "The Sims 4 Realm of Magic", Url = "https://file-eu-par-1.gofile.io/download/web/8aafad37-ce11-4c7b-bcac-18fb550937be/Sims4_DLC_GP08_Realm_of_Magic.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP09", Name = "The Sims 4 Star Wars: Journey to Batuu", Url = "https://file-na-lax-1.gofile.io/download/web/a32080ee-1b9c-4ef8-995f-d21dea5ea7ac/Sims4_DLC_GP09_Star_Wars_Journey_to_Batuu.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP10", Name = "The Sims 4 Dream Home Decorator", Url = "https://file-eu-par-2.gofile.io/download/web/4d14bdb7-cb41-4d26-9894-ddca2fd91a99/Sims4_DLC_GP10_Dream_Home_Decorator.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP11", Name = "The Sims 4 My Wedding Stories", Url = "https://file-na-lax-1.gofile.io/download/web/168b76ff-84f7-44a9-827b-73828910ca65/Sims4_DLC_GP11_My_Wedding_Stories.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP12", Name = "The Sims 4 Werewolves", Url = "https://file-eu-par-2.gofile.io/download/web/3d3b71d4-bb78-4e59-af3b-5e02244ffe0b/Sims4_DLC_GP12_Werewolves.zip", Installed = false });
            //Akcesoria (SP)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP01", Name = "The Sims 4 Luxury Party Stuff", Url = "https://file-na-chi-1.gofile.io/download/web/173b2422-05d7-4ccc-8f4b-3eb0cef98bf6/Sims4_DLC_SP01_Luxury_Party_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP02", Name = "The Sims 4 Perfect Patio Stuff", Url = "https://file-eu-par-1.gofile.io/download/web/3a9ac093-406e-4a5d-a389-4c2250e4cd2f/Sims4_DLC_SP02_Perfect_Patio_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP03", Name = "The Sims 4 Cool Kitchen Stuff", Url = "https://file-eu-par-1.gofile.io/download/web/965c597a-45ee-4fcb-9ef3-15da34219696/Sims4_DLC_SP03_Cool_Kitchen_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP04", Name = "The Sims 4 Spooky Stuff", Url = "https://file-eu-par-5.gofile.io/download/web/c4582530-564e-49dc-b086-0c01da969f75/Sims4_DLC_SP04_Spooky_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP05", Name = "The Sims 4 Movie Hangout Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/80a2007e-b901-46cf-be67-cdf350517c29/Sims4_DLC_SP05_Movie_Hangout_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP06", Name = "The Sims 4 Romantic Garden Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/8c68c21d-ac07-43b9-b8bf-c40567628d7f/Sims4_DLC_SP06_Romantic_Garden_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP07", Name = "The Sims 4 Kids Room Stuff", Url = "https://file-eu-par-1.gofile.io/download/web/d0960e00-3b5f-4e3d-b4d2-3f78c3f16f7c/Sims4_DLC_SP07_Kids_Room_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP08", Name = "The Sims 4 Backyard Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/e4541723-3050-4cea-9528-2e724d8b48bd/Sims4_DLC_SP08_Backyard_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP09", Name = "The Sims 4 Vintage Glamour Stuff", Url = "https://file-eu-par-1.gofile.io/download/web/af8b5500-7ac9-4221-a8d2-301bd870f3f7/Sims4_DLC_SP09_Vintage_Glamour_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP10", Name = "The Sims 4 Bowling Night Stuff", Url = "https://file-eu-par-5.gofile.io/download/web/8bcf01d6-4c49-4d40-aadc-5ed5b8b0a66a/Sims4_DLC_SP10_Bowling_Night_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP11", Name = "The Sims 4 Fitness Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/c17af5de-9a45-4ab4-9fbb-1c8b524ba7ce/Sims4_DLC_SP11_Fitness_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP12", Name = "The Sims 4 Toddler Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/a024e223-54f0-4e66-991c-97c5b850e5b5/Sims4_DLC_SP12_Toddler_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP13", Name = "The Sims 4 Laundry Day Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/cbc9388a-1b6b-42e8-88b4-e127be90363a/Sims4_DLC_SP13_Laundry_Day_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP14", Name = "The Sims 4 My First Pet Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/973b0abb-814c-4d46-8df8-1310f6aa74e3/Sims4_DLC_SP14_My_First_Pet_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP15", Name = "The Sims 4 Moschino Stuff", Url = "https://file-na-lax-1.gofile.io/download/web/dd2a8593-d3e0-46ba-81ee-160f6475b2ee/Sims4_DLC_SP15_Moschino_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP16", Name = "The Sims 4 Tiny Living Stuff", Url = "https://file-eu-par-1.gofile.io/download/web/f9e1ad30-0f77-4201-b0d2-67b21cb6d679/Sims4_DLC_SP16_Tiny_Living_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP17", Name = "The Sims 4 Nifty Knitting Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/8bb46416-c655-40cc-bd92-a8913b1aba0e/Sims4_DLC_SP17_Nifty_Knitting.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP18", Name = "The Sims 4 Paranormal Stuff", Url = "https://file-na-phx-1.gofile.io/download/web/9b883bd6-d3b8-46e7-a73c-9038bdc2e72c/Sims4_DLC_SP18_Paranormal_Stuff_Pack.zip", Installed = false });
            // Kolekcje (Kits)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP20", Name = "The Sims 4 Throwback Fit Kit", Url = "https://file-na-phx-1.gofile.io/download/web/d5018b89-b46c-4216-bcfe-525fdf4f377d/Sims4_DLC_SP20_Throwback_Fit_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP21", Name = "The Sims 4 Country Kitchen Kit", Url = "https://file-eu-par-5.gofile.io/download/web/4f410f05-08b6-4277-895c-ed7b0df77600/Sims4_DLC_SP21_Country_Kitchen_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP22", Name = "The Sims 4 Bust the Dust Kit", Url = "https://file-eu-par-5.gofile.io/download/web/d6f2343a-6332-4d44-98f2-4ebdb091999b/Sims4_DLC_SP22_Bust_the_Dust_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP23", Name = "The Sims 4 Courtyard Oasis Kit", Url = "https://file-na-phx-1.gofile.io/download/web/f068ca34-268b-4e51-8ddf-8948acd1bb85/Sims4_DLC_SP23_Courtyard_Oasis_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP24", Name = "The Sims 4 Fashion Street Kit", Url = "https://file-eu-par-2.gofile.io/download/web/3d2925ad-a526-40e7-9081-6b312a4b5a1f/Sims4_DLC_SP24_Fashion_Street_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP25", Name = "The Sims 4 Industrial Loft Kit", Url = "https://file-eu-par-1.gofile.io/download/web/c955de01-8d67-4012-be0c-ba8583182547/Sims4_DLC_SP25_Industrial_Loft_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP26", Name = "The Sims 4 Incheon Arrivals Kit", Url = "https://file-eu-par-1.gofile.io/download/web/b7e2727e-7dde-4194-bfae-970e4109c130/Sims4_DLC_SP26_Incheon_Arrivals_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP28", Name = "The Sims 4 Modern Menswear Kit", Url = "https://file-na-phx-1.gofile.io/download/web/d961df99-d513-4673-bc69-726ebd831a38/Sims4_DLC_SP28_Modern_Menswear_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP29", Name = "The Sims 4 Blooming Rooms Kit", Url = "https://file-eu-par-1.gofile.io/download/web/e1f403e2-4eb7-4751-bd4e-0bfdc69cad86/Sims4_DLC_SP29_Blooming_Rooms_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP30", Name = "The Sims 4 Carnaval Streetwear Kit", Url = "https://file-na-phx-1.gofile.io/download/web/b7a6b497-ed92-406f-bddc-755468f5a5d0/Sims4_DLC_SP30_Carnaval_Streetwear_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP31", Name = "The Sims 4 Décor to the Max Kit", Url = "https://file-eu-par-5.gofile.io/download/web/b8200806-7c2c-4b4e-af05-501a4b1e490f/Sims4_DLC_SP31_Decor_to_the_Max_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP32", Name = "The Sims 4 Moonlight Chic Kit", Url = "https://file-na-phx-1.gofile.io/download/web/d1ad5266-e0e0-4ed9-b5a3-1ea1ec25a324/Sims4_DLC_SP32_Moonlight_Chic_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP33", Name = "The Sims 4 Little Campers Kit", Url = "https://file-na-phx-1.gofile.io/download/web/f48c1a26-5730-4ed2-a3bd-23ef1ae8e025/Sims4_DLC_SP33_Little_Campers_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP34", Name = "The Sims 4 First Fits Kit", Url = "https://file-eu-par-2.gofile.io/download/web/9ef0787e-a330-4906-8378-cf95e0a38987/Sims4_DLC_SP34_First_Fits_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP35", Name = "The Sims 4 Desert Luxe Kit", Url = "https://file-eu-par-5.gofile.io/download/web/c2291570-5174-4d4b-a96b-4ab3e1a9b8f4/Sims4_DLC_SP35_Desert_Luxe_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP36", Name = "The Sims 4 Pastel Pop Kit", Url = "https://file-eu-par-1.gofile.io/download/web/b60a3b00-c102-4d30-a49c-348d5a62994a/Sims4_DLC_SP36_Pastel_Pop_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP37", Name = "The Sims 4 Everyday Clutter Kit", Url = "https://file-eu-par-2.gofile.io/download/web/9c68c648-49e3-4fb6-bdb7-1dd181ca942d/Sims4_DLC_SP37_Everyday_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP38", Name = "The Sims 4 Simtimates Collection Kit", Url = "https://file-na-phx-1.gofile.io/download/web/b7afe264-b116-417c-8dd0-15e6b6e4e1d2/Sims4_DLC_SP38_Simtimates_Collection_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP39", Name = "The Sims 4 Bathroom Clutter Kit", Url = "https://file-na-phx-1.gofile.io/download/web/8cf14c5c-1138-48a9-aeb6-d1a59d3460e7/Sims4_DLC_SP39_Bathroom_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP40", Name = "The Sims 4 Greenhouse Haven Kit", Url = "https://file-na-phx-1.gofile.io/download/web/f60e2c7d-5b37-4e9f-be76-5e341a5482aa/Sims4_DLC_SP40_Greenhouse_Haven_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP41", Name = "The Sims 4 Basement Treasures Kit", Url = "https://file-eu-par-5.gofile.io/download/web/b8e9ffb6-a87d-427d-bd55-883dd414e8b2/Sims4_DLC_SP41_Basement_Treasures_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP42", Name = "The Sims 4 Grunge Revival Kit", Url = "https://file-eu-par-2.gofile.io/download/web/d3b8e53f-0ef3-4260-b96e-17d33ab3ab40/Sims4_DLC_SP42_Grunge_Revival_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP43", Name = "The Sims 4 Book Nook Kit", Url = "https://file-na-phx-1.gofile.io/download/web/ea903b14-e893-40d8-a579-e61f8bb3434e/Sims4_DLC_SP43_Book_Nook_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP44", Name = "The Sims 4 Poolside Splash Kit", Url = "https://file-na-lax-1.gofile.io/download/web/e37c1317-1abd-455d-8ca1-06fdd5709d3c/Sims4_DLC_SP44_Poolside_Splash_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP45", Name = "The Sims 4 Modern Luxe Kit", Url = "https://file-eu-par-5.gofile.io/download/web/ce07d3b6-a94c-4709-adb2-6ad83d43bf93/Sims4_DLC_SP45_Modern_Luxe_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP46", Name = "The Sims 4 Home Chef Hustle Stuff", Url = "https://file-eu-par-2.gofile.io/download/web/80bdd3b1-f49f-486a-9399-85f1f19336d2/Sims4_DLC_SP46_Home_Chef_Hustle_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP47", Name = "The Sims 4 Castle Estate Kit", Url = "https://file-na-phx-1.gofile.io/download/web/cb4871a9-e214-4ebc-87af-e33173db1c77/Sims4_DLC_SP47_Castle_Estate_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP48", Name = "The Sims 4 Goth Galore Kit", Url = "https://file-na-phx-1.gofile.io/download/web/8099770c-18ea-4425-b468-5c0e98666111/Sims4_DLC_SP48_Goth_Galore_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP49", Name = "The Sims 4 Crystal Creations Stuff", Url = "https://file-eu-par-2.gofile.io/download/web/e0ca7332-041b-4de5-a376-b2df12e1f164/Sims4_DLC_SP49_Crystal_Creations_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP50", Name = "The Sims 4 Urban Homage Kit", Url = "https://file-na-phx-1.gofile.io/download/web/dee7962e-6c68-4b1f-abc7-43d206db6d98/Sims4_DLC_SP50_Urban_Homage_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP51", Name = "The Sims 4 Party Essentials Kit", Url = "https://file-na-lax-1.gofile.io/download/web/99be5f2d-ca74-454c-8305-8ad014e37d41/Sims4_DLC_SP51_Party_Essentials_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP52", Name = "The Sims 4 Riviera Retreat Kit", Url = "https://file-eu-par-2.gofile.io/download/web/81fec8ce-4bd2-4f4d-a8ae-6eca5d8cdabd/Sims4_DLC_SP52_Riviera_Retreat_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP53", Name = "The Sims 4 Cozy Bistro Kit", Url = "https://file-na-phx-1.gofile.io/download/web/c76acfa3-2b11-4136-8a95-1d7587f6e7f2/Sims4_DLC_SP53_Cozy_Bistro_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP54", Name = "The Sims 4 Artist Studio Kit", Url = "https://file-na-phx-1.gofile.io/download/web/e4e8d087-3272-446e-b0ea-e8475a3be7af/Sims4_DLC_SP54_Artist_Studio_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP55", Name = "The Sims 4 Storybook Nursery Kit", Url = "https://file-eu-par-2.gofile.io/download/web/ea7eb368-b653-44a4-b69f-d360b77d6c0c/Sims4_DLC_SP55_Storybook_Nursery_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP56", Name = "The Sims 4 Sweet Slumber Party Kit", Url = "https://file-na-phx-1.gofile.io/download/web/f269e7aa-12f0-42ab-b021-01f8468a7308/Sims4_DLC_SP56_Sweet_Slumber_Party_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP57", Name = "The Sims 4 Cozy Kitsch Kit", Url = "https://file-na-phx-1.gofile.io/download/web/b3157edf-8916-42c7-b69a-1a5f14105fb4/Sims4_DLC_SP57_Cozy_Kitsch_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP58", Name = "The Sims 4 Comfy Gamer Kit", Url = "https://file-eu-par-1.gofile.io/download/web/fa7c0631-f582-46c3-bb29-6307e47f4c38/Sims4_DLC_SP58_Comfy_Gamer_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP59", Name = "The Sims 4 Secret Sanctuary Kit", Url = "https://file-na-phx-1.gofile.io/download/web/9f48982f-a949-4544-8374-de1ce24920a2/Sims4_DLC_SP59_Secret_Sanctuary_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP60", Name = "The Sims 4 Casanova Cave Kit", Url = "https://file-eu-par-2.gofile.io/download/web/89244208-c339-4bfd-a667-3e328bfe0bca/Sims4_DLC_SP60_Casanova_Cave_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP61", Name = "The Sims 4 Refined Living Room Kit", Url = "https://file-na-phx-1.gofile.io/download/web/afa8fd43-825e-4c32-8462-a86a938fe6ae/Sims4_DLC_SP61_Refined_Living_Room_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP62", Name = "The Sims 4 Business Chic Kit", Url = "https://file-na-phx-1.gofile.io/download/web/e596bb38-dd98-4a8a-81d0-af6ac16ea71e/Sims4_DLC_SP62_Business_Chic_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP63", Name = "The Sims 4 Sleek Bathroom Kit", Url = "https://file-eu-par-5.gofile.io/download/web/ff7a7cd0-9297-4e91-9674-876b5a77e259/Sims4_DLC_SP63_Sleek_Bathroom_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP64", Name = "The Sims 4 Sweet Allure Kit", Url = "https://file-eu-ldn-1.gofile.io/download/web/bb70e90b-8cc7-403c-b8d7-2a828ff2372e/Sims4_DLC_SP64_Sweet_Allure_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP65", Name = "The Sims 4 Restoration Workshop Kit", Url = "https://file-eu-par-1.gofile.io/download/web/f251250f-3e52-4cd0-93ab-f36482bf1bbc/Sims4_DLC_SP65_Restoration_Workshop_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP66", Name = "The Sims 4 Golden Years Kit", Url = "https://file-na-chi-1.gofile.io/download/web/ea90e808-99be-4556-bdb1-93bb29e78a94/Sims4_DLC_SP66_Golden_Years_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP67", Name = "The Sims 4 Kitchen Clutter Kit", Url = "https://file-eu-par-1.gofile.io/download/web/b5773932-bc4b-4c16-a686-8535cb96090c/Sims4_DLC_SP67_Kitchen_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP68", Name = "The Sims 4 SpongeBob’s House Kit", Url = "https://file-eu-par-2.gofile.io/download/web/aa0a398a-bf12-42f9-b125-7225e638a034/Sims4_DLC_SP68_SpongeBob_House_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP69", Name = "The Sims 4 Autumn Apparel Kit", Url = "https://file-eu-par-5.gofile.io/download/web/aab049fb-0907-49fc-901c-76c4692d13db/Sims4_DLC_SP69_Autumn_Apparel_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP70", Name = "The Sims 4 SpongeBob Kid’s Room Kit", Url = "https://file-na-phx-1.gofile.io/download/web/a8d418f6-9e41-4826-83ce-cee22deef8f3/Sims4_DLC_SP70_SpongeBob_Kid_Room_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP71", Name = "The Sims 4 Grange Mudroom Kit", Url = "https://file-na-chi-1.gofile.io/download/web/a4f08473-5142-43e2-9294-a18d4b1d6166/Sims4_DLC_SP71_Grange_Mudroom_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP72", Name = "The Sims 4 Essential Glam Kit", Url = "https://file-eu-par-2.gofile.io/download/web/ab73998d-3d9e-4175-9181-1c527f3ab491/Sims4_DLC_SP72_Essential_Glam_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP73", Name = "The Sims 4 Modern Retreat Kit", Url = "https://file-na-phx-1.gofile.io/download/web/b18991c7-1016-4bdd-a8b9-9dc2add7128a/Sims4_DLC_SP73_Modern_Retreat_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP74", Name = "The Sims 4 Garden to Table Kit", Url = "https://file-eu-par-2.gofile.io/download/web/ccb9b2fa-e89b-4f85-b8fa-85e37460043f/Sims4_DLC_SP74_Garden_to_Table_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP75", Name = "The Sims 4 Wonderland Playroom Kit", Url = "https://file-eu-par-5.gofile.io/download/web/98435180-285c-4274-b40f-f8d7d3c6cd65/SP75%20Wonderland%20Playroom%20Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP76", Name = "The Sims 4 Silver Screen Style Kit", Url = "https://file-eu-par-2.gofile.io/download/web/d05493a4-2123-4ba9-a62f-2c2b8e6f2276/SP76%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP77", Name = "The Sims 4 Tea Time Solarium Kit", Url = "https://file-na-phx-1.gofile.io/download/web/cfe42202-a840-4f7e-babb-c5d67fd84035/SP77%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP78", Name = "The Sims 4 Lady Bridgerton’s Masquerade Ball Fashion Kit", Url = "https://file-na-phx-1.gofile.io/download/web/b2930684-0476-4048-b4ba-1a199ad47076/SP78%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP79", Name = "The Sims 4 Lady Bridgerton’s Masquerade Ballroom Kit", Url = "https://file-eu-par-5.gofile.io/download/web/c12f7e80-ab6d-4302-9d60-db54e7494999/SP79%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP80", Name = "The Sims 4 Music Den Kit", Url = "https://file-eu-par-2.gofile.io/download/web/c0016ba7-941d-40b3-9b1e-2788303d9fb3/SP80%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP81", Name = "The Sims 4 Prairie Dreams Set", Url = "https://file-na-phx-1.gofile.io/download/web/d56422e3-ad6c-4284-b976-6d0826007406/Sims4_DLC_SP81_Prairie_Dreams_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP82", Name = "The Sims 4 Yard Charm (Creator) Kit", Url = "https://file-eu-par-2.gofile.io/download/web/ddfb1c96-05de-436a-a584-e49d40d8169f/SP82%20Yard%20Charm%20Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP83", Name = "The Sims 4 Mean Girls Capsule Kit", Url = "https://file-na-phx-1.gofile.io/download/web/a545c64a-e805-4b1d-be9a-69f58d36cced/SP83%200.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP84", Name = "The Sims 4 Clueless Capsule  Kit", Url = "https://file-eu-par-1.gofile.io/download/web/a876e647-e4ce-41d9-839b-cf12c4b8362d/SP84%200.zip", Installed = false });
        }


        public async Task DownloadAndInstallFromMinioSelectedDlcsAsync(Logger logger, string gamepath)
        {

            foreach (var dlc in Sims4DLCs)
            {
                if (dlc.ToInstall && !dlc.Installed)
                {
                    logger.AddLog($"Downloading DLC: {dlc.Code} - {dlc.Name}");


                    bool success = await dlc.Download(logger);

                    if (!success)
                    {
                        logger.AddLog($"❌ Failed to download DLC: {dlc.Code} - {dlc.Name}");
                        logger.AddLog("Skipping this DLC and continuing with next one...");
                        logger.AddLog("---------------------------------------------");
                        continue;
                    }

                    logger.AddLog($"Extracting DLC: {dlc.Code} - {dlc.Name}");
                    dlc.Extract(logger);

                    logger.AddLog($"Installing DLC: {dlc.Code} - {dlc.Name}");
                    dlc.Install(gamepath, logger);

                    logger.AddLog($"✓ Installed DLC: {dlc.Code} - {dlc.Name}");
                    logger.AddLog($"Clearing TEMP files");
                    await Task.Run(() => dlc.Remove(logger));

                    logger.AddLog("---------------------------------------------");
                }
            }

            logger.AddLog("All downloads completed.");
        }
      

    }
}
