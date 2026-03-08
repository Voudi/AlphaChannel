#!/bin/bash
GIST="77c661ec1597925c2856d649a0cfc2d8"
GISTTEST="ff56526cfcd0197a87d518e07cf1302f"

git clone https://gist.github.com/Voudi/$GIST.git

cp pluginmaster.json $GIST/pluginmaster.json
cd $GIST
git show-ref 
git add pluginmaster.json
git commit -m "Adding Zip to Gist"
 
git push origin main
#If prompted, provide your Github credentials associated with the gist
cd ..

git clone https://gist.github.com/Voudi/$GISTTEST.git

cp AlphaChannel.zip $GISTTEST/AlphaChannel.zip
cd $GISTTEST
NEW_COMMIT=$(git commit-tree HEAD^{tree} -m "Reset Gist to latest state")
git reset --hard $NEW_COMMIT
git show-ref 
git add AlphaChannel.zip

git commit -m "Adding Zip to Gist"
 
git push --force origin main

cd ..

rm -rf $GIST
rm -rf $GISTTEST
rm -rf AlphaChannel.zip
rm -rf pluginmaster.json