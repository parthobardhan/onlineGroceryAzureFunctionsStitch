using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Generic;

namespace MongoOnlineGrocery
{
    public class ProductRating
    {
        [BsonElement("Rating")]
        public int Rating { get; set; }

    }
    public class Product
    {
        [BsonId]
        public ObjectId Id { get; set; }
        [BsonElement("PLU")]
        public int PLU { get; set; }
        [BsonElement("Description")]
        public string Description { get; set; }
        [BsonElement("AverageRating")]
        public Double AvgRating { get; set; }
        [BsonElement("Ratings")]
        public List<ProductRating> Ratings { get; set; }

    }
    public static class UpdateCart
    {
        //This function is called locally via:  http://localhost:7071/api/ResetDemo
        //Its purpose is to clear the MongoOnlineGrocery database, create an index on the PLU field
        //and insert a few sample items

        [FunctionName("ResetDemo")]
        public static async Task<IActionResult> RunGet([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]HttpRequest req, ILogger log)
        {
            try
            {

                var client = new MongoClient(System.Environment.GetEnvironmentVariable("MongoDBAtlasConnectionString"));
                var database = client.GetDatabase("MongoOnlineGrocery");
                var collection = database.GetCollection<Product>("inventory");
                //We could also just drop the collection
                await collection.DeleteManyAsync(new BsonDocument { });
                await collection.Indexes.DropAllAsync();

                //Create an index on the PLU, this will also enforce uniqueness
                IndexKeysDefinition<Product> keys = "{ PLU: 1 }";
                var indexModel = new CreateIndexModel<Product>(keys);
                await collection.Indexes.CreateOneAsync(indexModel);

                //Create a default rating of 3 for new products
                var MyRating = new ProductRating();
                MyRating.Rating = 3;
                var DefaultProductRating = new List<ProductRating>();
                DefaultProductRating.Add(MyRating);

                //Define some sample objects
                var Bananas = new Product
                {
                    Id = new ObjectId(),
                    PLU = 4011,
                    Description = "Bananas",
                    Ratings = DefaultProductRating
                };
                var Apples = new Product
                {
                    Id = new ObjectId(),
                    PLU = 3283,
                    Description = "Apples",
                    Ratings = DefaultProductRating
                };

                //MongoDB makes it easy to go from object to database with no ORM layer
                await collection.InsertOneAsync(Bananas);
                await collection.InsertOneAsync(Apples);

            }
            catch (Exception e)
            {
                return new BadRequestObjectResult("Error refreshing demo - " + e.Message);

            }

            return (ActionResult)new OkObjectResult("Refreshed Demo database");
        }
        [FunctionName("ProductReview")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)]HttpRequest req, ILogger log)
        {
            int iPLU = 0;
            int iRating = 0;
            Double iAvg = 0;

            if (Int32.TryParse(req.Query["PLU"], out iPLU))
            {
                iPLU = Int32.Parse(req.Query["PLU"]);
            }
            else
            {
                return new BadRequestObjectResult("Please pass a PLU parameter");
            }

            if (Int32.TryParse(req.Query["Rating"], out iRating))
            {
                iRating = Int32.Parse(req.Query["Rating"]);
                if (iRating < 1 || iRating > 5)
                {
                    return new BadRequestObjectResult("Rating must be between 1 and 5");
                }
            }
            else
            {
                return new BadRequestObjectResult("Please pass a Rating parameter");
            }

            //Create client connection to our MongoDB Atlas database
            var client = new MongoClient(System.Environment.GetEnvironmentVariable("MongoDBAtlasConnectionString"));

            //Create a session object that is used when leveraging transactions
            var session = client.StartSession();

            //Create the collection object that represents the "inventory" collection
            var collection = session.Client.GetDatabase("MongoOnlineGrocery").GetCollection<Product>("inventory");

            //Begin transaction
            session.StartTransaction();

            //Append the rating to the PLU
            var filter = new FilterDefinitionBuilder<Product>().Where(r => r.PLU == iPLU);

            //For now, our ratings are just an array of integers, in future we could easily add more metadata
            var MyRating = new ProductRating();
            MyRating.Rating = iRating;

            var update = Builders<Product>.Update.Push<ProductRating>(r => r.Ratings, MyRating);
            var options = new UpdateOptions() { IsUpsert = true };

            try
            {
                //Add the rating to our product
                await collection.UpdateOneAsync(filter, update, options);

                //Calculate the average rating
                /* Equivalent Mongo Query Language statement:
                 * 
                 * db.inventory.aggregate( [
                 * { $match: { "PLU":4011 }},
                 * { $unwind: "$Ratings" },
                 * { $group: { _id: "$_id", AvgRating: { $avg: "$Ratings.Rating" }}}
                 * ])
                 */

                //Building out the Group pipeline stage
                List<BsonElement> e = new List<BsonElement>();
                e.Add(new BsonElement("_id", "$_id"));
                e.Add(new BsonElement("AvgRating", new BsonDocument("$avg", "$Ratings.Rating")));


                PipelineDefinition<Product, BsonDocument> Pipe = new BsonDocument[]
                {
                    new BsonDocument {{ "$match", new BsonDocument("PLU", iPLU)}},
                    new BsonDocument {{ "$unwind", new BsonString("$Ratings")}},
                    new BsonDocument {{ "$group", new BsonDocument(e)}}

                };
                var AverageRating = await collection.AggregateAsync<BsonDocument>(Pipe);

                //We filtered it down to only average a specific PLU
                var o = AverageRating.First();
                iAvg = o["AvgRating"].AsDouble;

                //Now that we calculated the average update the PLU with the latest average
                var updateavg = Builders<Product>.Update.Set(r => r.AvgRating, iAvg);
                await collection.UpdateOneAsync(filter, updateavg);

                //Made it here without error? Let's commit the transaction
                session.CommitTransaction();

            }
            catch (Exception e)
            {
                session.AbortTransaction();
                return new BadRequestObjectResult("Error: " + e.Message);
            }


            return iRating > 0
                ? (ActionResult)new OkObjectResult($"Added Rating of {iRating} to {iPLU}.  Average rating is {iAvg}")
                : new BadRequestObjectResult("Please pass a PLU and Quantity as parameters");
        }
    }
}